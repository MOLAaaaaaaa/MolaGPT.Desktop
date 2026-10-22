using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Models;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Remote;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services.Remote;

public enum RemoteControlState
{
    Off,
    Connecting,
    Online,
    Offline
}

/// <summary>
/// Remote control of this desktop's conversations from paired phones.
///
/// The desktop is the only place conversations live. Phones reach it through the
/// account's realtime relay, which forwards frames between the two and stores
/// nothing; the HTTP side only carries the conversation list and a heartbeat, so
/// the phone can show the computer while it is asleep. Protocol: the phone repo's
/// <c>docs/desktop-remote-design.md</c>.
/// </summary>
public sealed class RemoteControlHost : IRemoteApprovalChannel, IAsyncDisposable
{
    private const int Protocol = 1;
    private const int ListLimit = 50;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    private const int PairingAttempts = 5;
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private const long MaxUploadBytes = 32L * 1024 * 1024;
    private const int UploadChunkBytes = 256 * 1024;
    private const int MaxUploadsPerPhone = 12;
    private const int MaxAttachmentsPerMessage = 20;
    private static readonly TimeSpan UploadIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StagedLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly IWebProxy? _proxy;
    private readonly MolaGptAuthService _auth;
    private readonly ChatViewModel _chat;
    private readonly ComposerViewModel _composer;
    private readonly MessageRepository _messages;
    private readonly ConversationRepository _conversations;
    private readonly ProviderRegistry _providers;
    private readonly PairedPhoneStore _phones;
    private readonly RemoteApprovalRelay _approvalRelay;
    private readonly Func<Func<Task>, Task> _onUi;
    private readonly Action<string>? _log;
    private readonly string _base;
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly string _appVersion;
    private readonly RelayCommandJournal _journal;

    private readonly object _gate = new();
    // peer -> conversation -> subscription
    private readonly Dictionary<string, Dictionary<string, Subscription>> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _peerQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _remoteTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _seenRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly string _uploadDirectory;
    private HashSet<string> _listed = new(StringComparer.Ordinal);
    private Pairing? _pairing;

    private Channel<byte[]>? _outbox;
    private readonly Channel<(string ConversationId, string MessageId)> _writes =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _listSignal = new(0);
    private long _listDueAtMs;
    private string? _lastPublishedList;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private RemoteControlState _state = RemoteControlState.Off;

    public RemoteControlHost(
        HttpClient http,
        MolaGptAuthService auth,
        ChatViewModel chat,
        ComposerViewModel composer,
        MessageRepository messages,
        ConversationRepository conversations,
        ProviderRegistry providers,
        PairedPhoneStore phones,
        RemoteApprovalRelay approvalRelay,
        Func<Func<Task>, Task> onUi,
        string machineId,
        string machineName,
        Action<string>? log = null,
        string? baseUrl = null,
        IWebProxy? proxy = null)
    {
        _http = http;
        _proxy = proxy;
        _auth = auth;
        _chat = chat;
        _composer = composer;
        _messages = messages;
        _conversations = conversations;
        _providers = providers;
        _phones = phones;
        _approvalRelay = approvalRelay;
        _onUi = onUi;
        _machineId = machineId;
        _machineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName.Trim();
        _log = log;
        _base = baseUrl ?? "https://chatgpt.wljay.cn/v2";
        _appVersion = typeof(RemoteControlHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT");
        var journalDirectory = Path.Combine(dataRoot, "remote-command-journal");
        PruneJournal(journalDirectory);
        _journal = new RelayCommandJournal(journalDirectory);
        _uploadDirectory = Path.Combine(dataRoot, "remote-uploads");
        ClearUploadDirectory();

        _messages.MessageWritten += (conversationId, messageId) =>
        {
            if (HasSubscribers(conversationId)) _writes.Writer.TryWrite((conversationId, messageId));
        };
        _messages.ConversationRewritten += OnConversationRewritten;
        _conversations.TimelineChanged += OnConversationRewritten;
        _conversations.ConversationChanged += OnConversationChanged;
        // Switches flipped on the desktop reach a phone watching the same conversation.
        _composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComposerViewModel.EnableNetwork) or nameof(ComposerViewModel.EnableThinking)
                or nameof(ComposerViewModel.ReasoningEffort))
                PushSettingsOnScreen();
        };
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.ActiveModel)) PushSettingsOnScreen();
        };
    }

    public RemoteControlState State
    {
        get { lock (_gate) return _state; }
    }

    public event Action? StateChanged;

    /// <summary>The code to show while pairing, or null.</summary>
    public string? PairingCode
    {
        get { lock (_gate) return _pairing is { } p && !p.Expired ? p.Code : null; }
    }

    public DateTimeOffset? PairingExpiresAt
    {
        get { lock (_gate) return _pairing is { } p && !p.Expired ? p.ExpiresAt : null; }
    }

    public event Action? PairingChanged;

    /// <summary>
    /// A phone finished pairing: its name, and whether it paired through the account
    /// rather than a code typed at this desk.
    /// </summary>
    public event Action<string, bool>? Paired;

    public PairedPhoneStore Phones => _phones;

    /// <summary>Whether a phone has talked to this desktop over the current connection.</summary>
    public bool HasConnectedPhones
    {
        get { lock (_gate) return _peerQueues.Count > 0; }
    }

    public event Action? ConnectedPhonesChanged;

    // ---- lifecycle ------------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(() => RunAsync(token));
        }
        _approvalRelay.Attach(this);
        SetState(RemoteControlState.Connecting);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_gate)
        {
            cts = _cts;
            run = _run;
            _cts = null;
            _run = null;
        }
        if (cts is null) return;
        _approvalRelay.Attach(null);
        cts.Cancel();
        // The loops may be waiting on the UI thread, which may be the one stopping us.
        if (run is not null) await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        cts.Dispose();
        CancelPairing();
        foreach (var pending in TakeAllApprovals()) pending.Tcs.TrySetResult(RemoteApprovalDecisions.Deny);
        DropUploads(_ => true);
        using var offline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await PostDirectoryAsync(new JsonObject { ["kind"] = "offline", ["machineId"] = _machineId }, offline.Token).ConfigureAwait(false); }
        catch (Exception ex) { Log($"offline: {ex.Message}"); }
        SetState(RemoteControlState.Off);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken ct)
    {
        ScheduleList(TimeSpan.Zero);
        await Task.WhenAll(
            SocketLoopAsync(ct),
            DirectoryLoopAsync(ct),
            WritePumpAsync(ct),
            StateTickAsync(ct)).ConfigureAwait(false);
    }

    private void SetState(RemoteControlState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        StateChanged?.Invoke();
    }

    // ---- pairing --------------------------------------------------------------

    private sealed class Pairing(string code)
    {
        public string Code { get; } = code;
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow + PairingLifetime;
        public int AttemptsLeft { get; set; } = PairingAttempts;
        public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    /// <summary>Show a new pairing code; any earlier one stops working.</summary>
    public string BeginPairing()
    {
        string code;
        lock (_gate)
        {
            code = RemotePairing.NewCode();
            _pairing = new Pairing(code);
        }
        PairingChanged?.Invoke();
        return code;
    }

    public void CancelPairing()
    {
        lock (_gate)
        {
            if (_pairing is null) return;
            _pairing = null;
        }
        PairingChanged?.Invoke();
    }

    public void Unpair(string pairId) => _phones.Remove(pairId);

    private JsonObject HandlePair(JsonObject args, bool accountPairing)
    {
        var phoneKey = Text(args, "pub") ?? throw new RemoteError("bad_request", "缺少配对数据");
        var mac = Text(args, "mac");
        var device = Text(args, "device") is { Length: > 0 } name ? name[..Math.Min(name.Length, 60)] : "手机";
        var deviceId = Text(args, "deviceId");

        var code = RemotePairing.AccountPairingContext;
        if (!accountPairing)
        {
            lock (_gate)
            {
                if (_pairing is not { Expired: false } pairing)
                    throw new RemoteError("no_pairing", "配对码未生成或已过期，请在电脑端重新生成");
                if (!RemotePairing.MacMatches(pairing.Code, phoneKey, mac))
                {
                    pairing.AttemptsLeft--;
                    if (pairing.AttemptsLeft <= 0) _pairing = null;
                    throw new RemoteError("bad_code", pairing.AttemptsLeft <= 0 ? "配对尝试次数过多，请在电脑端重新生成配对码" : "配对码不正确");
                }
                code = pairing.Code;
                _pairing = null;
            }
            PairingChanged?.Invoke();
        }

        string desktopKey;
        byte[] secret;
        try { (desktopKey, secret) = RemotePairing.Agree(code, phoneKey); }
        catch (Exception) { throw new RemoteError("bad_request", "配对数据无效，请重试"); }
        var pairId = RemotePairing.NewPairId();
        _phones.Add(pairId, device, deviceId, secret);
        Log($"paired {device}");
        Paired?.Invoke(device, accountPairing);
        return new JsonObject
        {
            ["pairId"] = pairId,
            ["pub"] = desktopKey,
            ["mac"] = Convert.ToBase64String(accountPairing
                ? HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{RemotePairing.AccountPairingContext}|{phoneKey}|{desktopKey}|{pairId}"))
                : RemotePairing.Mac(code, phoneKey + "|" + desktopKey + "|" + pairId)),
            ["machineName"] = _machineName
        };
    }

    // ---- realtime socket ------------------------------------------------------

    private async Task SocketLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var renewed = false;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var socket = new ClientWebSocket();
                if (_proxy is not null) socket.Options.Proxy = _proxy;
                socket.Options.SetRequestHeader("User-Agent", UserAgentProvider.FixedUa);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + await GetTicketAsync(ct).ConfigureAwait(false));
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                var uri = new UriBuilder(_base + "/agent-realtime") { Scheme = new Uri(_base).Scheme == "https" ? "wss" : "ws" };
                await socket.ConnectAsync(uri.Uri, ct).ConfigureAwait(false);
                attempt = 0;
                var outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
                lock (_gate) _outbox = outbox;
                SetState(RemoteControlState.Online);
                Log("connected");
                var writer = WriteLoopAsync(socket, outbox.Reader, connection.Token);
                renewed = await ReadLoopAsync(socket, ct).ConfigureAwait(false);
                connection.Cancel();
                try { await writer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log($"socket: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                bool hadPeers;
                lock (_gate)
                {
                    _outbox?.Writer.TryComplete();
                    _outbox = null;
                    // Peers are per connection; phones open their conversations again.
                    _subscriptions.Clear();
                    hadPeers = _peerQueues.Count > 0;
                    _peerQueues.Clear();
                }
                if (hadPeers) ConnectedPhonesChanged?.Invoke();
                // Uploads in flight belonged to peers of this connection; finished ones wait for a send.
                DropUploads(upload => upload.Staged is null);
                if (!ct.IsCancellationRequested) SetState(RemoteControlState.Offline);
            }

            // The relay recycles every socket after half an hour; that is not a failure.
            var delay = renewed ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(attempt++, 4)));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<string> GetTicketAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_base}/api/auth/agent_realtime.php?role=device&machineId={Uri.EscapeDataString(_machineId)}");
        AttachAuth(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("ticket").GetString() ?? throw new InvalidDataException("No ticket.");
    }

    private static async Task WriteLoopAsync(ClientWebSocket socket, ChannelReader<byte[]> outbox, CancellationToken ct)
    {
        await foreach (var frame in outbox.ReadAllAsync(ct).ConfigureAwait(false))
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    /// <summary>Reads until the socket closes. True when the relay asked for a renewal.</summary>
    private async Task<bool> ReadLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return socket.CloseStatusDescription == "renew";
            message.Write(buffer.AsSpan(0, result.Count));
            if (message.WrittenCount > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
            if (!result.EndOfMessage) continue;
            try { OnFrame(message.WrittenSpan); }
            catch (Exception ex) { Log($"frame: {ex.Message}"); }
            message.Clear();
        }
        return false;
    }

    private void OnFrame(ReadOnlySpan<byte> raw)
    {
        if (JsonNode.Parse(raw) is not JsonObject frame) return;
        if (Text(frame, "from") is { } peer && frame["body"] is JsonObject body)
        {
            _ = HandleRequestAsync(peer, body);
            return;
        }
        if (Text(frame, "kind") == "gone" && Text(frame, "peer") is { } gone)
        {
            bool lastPeer;
            lock (_gate)
            {
                _subscriptions.Remove(gone);
                lastPeer = _peerQueues.Remove(gone) && _peerQueues.Count == 0;
            }
            if (lastPeer) ConnectedPhonesChanged?.Invoke();
            DropUploads(upload => upload.Staged is null && upload.Peer == gone);
        }
    }

    private void SendToPeer(string peer, JsonObject body)
    {
        var frame = new JsonObject { ["to"] = peer, ["body"] = body };
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        lock (_gate) _outbox?.Writer.TryWrite(bytes);
    }

    // ---- requests -------------------------------------------------------------

    private sealed class RemoteError(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private async Task HandleRequestAsync(string peer, JsonObject body)
    {
        var id = Text(body, "id");
        var op = Text(body, "op");
        if (id is null || op is null) return;

        // One request at a time per phone, so an open and the close after it land in order.
        SemaphoreSlim queue;
        var firstPeer = false;
        lock (_gate)
        {
            if (!_peerQueues.TryGetValue(peer, out queue!))
            {
                firstPeer = _peerQueues.Count == 0;
                _peerQueues[peer] = queue = new SemaphoreSlim(1, 1);
            }
        }
        if (firstPeer) ConnectedPhonesChanged?.Invoke();
        await queue.WaitAsync().ConfigureAwait(false);
        string? opened = null;
        try
        {
            JsonNode? data;
            var args = ParseArgs(Text(body, "a"));
            if (op is "pair" or "pair_account")
            {
                // The relay only forwards terminal requests from this desktop's account.
                data = HandlePair(args, accountPairing: op == "pair_account");
            }
            else
            {
                var pairId = Authenticate(body, id, op);
                (data, opened) = op switch
                {
                    "hello" => (Hello(), null),
                    "open" => (await OpenAsync(peer, args).ConfigureAwait(false), Text(args, "conversationId")),
                    "close" => (Close(peer, args), null),
                    "older" => (await OlderAsync(args).ConfigureAwait(false), null),
                    "full" => (await FullAsync(args).ConfigureAwait(false), null),
                    "send" => (await SendAsync(pairId, args).ConfigureAwait(false), null),
                    "new_defaults" => (await NewDefaultsAsync(args).ConfigureAwait(false), null),
                    "create" => (await CreateAsync(pairId, args).ConfigureAwait(false), null),
                    "upload_begin" => (await UploadBeginAsync(peer, pairId, args).ConfigureAwait(false), null),
                    "upload_chunk" => (UploadChunk(pairId, args), null),
                    "upload_end" => (await UploadEndAsync(pairId, args).ConfigureAwait(false), null),
                    "upload_cancel" => (UploadCancel(pairId, args), null),
                    "stop" => (await StopTurnAsync(args).ConfigureAwait(false), null),
                    "approve" => (Approve(args), null),
                    "models" => (await ModelsAsync().ConfigureAwait(false), null),
                    "configure" => (await ConfigureAsync(args).ConfigureAwait(false), null),
                    _ => throw new RemoteError("unknown_op", "电脑端版本不支持此操作，请更新")
                };
            }
            SendToPeer(peer, new JsonObject { ["v"] = Protocol, ["re"] = id, ["ok"] = true, ["data"] = data });
            if (opened is not null) FinishOpening(peer, opened);
        }
        catch (RemoteError error)
        {
            Log($"{op} refused: {error.Code}");
            SendToPeer(peer, new JsonObject
            {
                ["v"] = Protocol, ["re"] = id, ["ok"] = false, ["code"] = error.Code, ["message"] = error.Message
            });
        }
        catch (Exception ex)
        {
            Log($"{op} failed: {ex.GetType().Name}: {ex.Message}");
            SendToPeer(peer, new JsonObject
            {
                ["v"] = Protocol, ["re"] = id, ["ok"] = false, ["code"] = "internal", ["message"] = "电脑端处理请求失败"
            });
        }
        finally
        {
            queue.Release();
        }
    }

    /// <summary>Checks the request is signed by a paired phone, and returns its pair.</summary>
    private string Authenticate(JsonObject body, string id, string op)
    {
        var pairId = Text(body, "pair") ?? string.Empty;
        var args = Text(body, "a") ?? string.Empty;
        var timestamp = body["ts"] is JsonValue value && value.TryGetValue<long>(out var ts) ? ts : 0;
        var secret = _phones.Secret(pairId)
                     ?? throw new RemoteError("unpaired", "配对已失效，请重新配对");
        if (!RemotePairing.SignatureMatches(secret, id, op, timestamp, pairId, args, Text(body, "sig")))
            throw new RemoteError("bad_signature", "身份验证失败，请重新配对");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - timestamp) > ClockSkew.TotalMilliseconds)
            throw new RemoteError("stale", "设备时间不一致，请校准后重试");
        lock (_gate)
        {
            if (_seenRequests.Count > 2000)
                foreach (var old in _seenRequests.Where(p => now - p.Value > ReplayWindow.TotalMilliseconds).Select(p => p.Key).ToList())
                    _seenRequests.Remove(old);
            if (!_seenRequests.TryAdd(pairId + ":" + id, now))
                throw new RemoteError("replay", "请求重复");
        }
        _phones.Touch(pairId);
        return pairId;
    }

    private JsonObject Hello() => new()
    {
        ["machineName"] = _machineName,
        ["appVersion"] = _appVersion,
        ["protocol"] = Protocol
    };

    // ---- conversation content -------------------------------------------------

    private sealed class Subscription
    {
        public long Seq;
        /// <summary>Events produced while the snapshot was being read; sent after it.</summary>
        public List<(string Event, JsonNode Data)>? Pending = [];
    }

    private sealed record ConversationInfo(JsonObject Json, bool Busy);

    private async Task<JsonObject> OpenAsync(string peer, JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var limit = Math.Clamp(Int(args, "limit") ?? 30, 1, 60);

        // Subscribe before reading, so nothing written in between is missed.
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(peer, out var mine))
                _subscriptions[peer] = mine = new(StringComparer.Ordinal);
            mine[conversationId] = new Subscription();
        }
        try
        {
            var info = await DescribeAsync(conversationId).ConfigureAwait(false);
            var rows = await Task.Run(() => _messages.List(conversationId)).ConfigureAwait(false);
            var (messages, hasOlder) = Page(rows, rows.Count, limit);
            lock (_gate) _busy[conversationId] = info.Busy;
            return new JsonObject
            {
                ["seq"] = 0,
                ["conversation"] = info.Json,
                ["messages"] = messages,
                ["hasOlder"] = hasOlder,
                ["approvals"] = new JsonArray(ApprovalsFor(conversationId).Select(p => (JsonNode)PromptJson(p.Prompt)).ToArray())
            };
        }
        catch
        {
            lock (_gate)
                if (_subscriptions.TryGetValue(peer, out var mine)) mine.Remove(conversationId);
            throw;
        }
    }

    private void FinishOpening(string peer, string conversationId)
    {
        List<(string Event, JsonNode Data)>? pending;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(peer, out var mine) || !mine.TryGetValue(conversationId, out var subscription))
                return;
            pending = subscription.Pending;
            subscription.Pending = null;
        }
        foreach (var (ev, data) in pending ?? []) PushTo(peer, conversationId, ev, data);
    }

    private JsonObject Close(string peer, JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        lock (_gate)
            if (_subscriptions.TryGetValue(peer, out var mine)) mine.Remove(conversationId);
        return new JsonObject();
    }

    private async Task<JsonObject> OlderAsync(JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var before = Text(args, "before") ?? throw new RemoteError("bad_request", "缺少历史消息位置");
        var limit = Math.Clamp(Int(args, "limit") ?? 30, 1, 60);
        var rows = await Task.Run(() => _messages.List(conversationId)).ConfigureAwait(false);
        var end = rows.ToList().FindIndex(row => row.Id == before);
        var (messages, hasOlder) = end < 0 ? (new JsonArray(), false) : Page(rows, end, limit);
        return new JsonObject { ["messages"] = messages, ["hasOlder"] = hasOlder };
    }

    private async Task<JsonObject> FullAsync(JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var messageId = Text(args, "messageId") ?? throw new RemoteError("bad_request", "缺少消息标识");
        var row = await Task.Run(() => _messages.Get(messageId)).ConfigureAwait(false);
        if (row is null || row.ConversationId != conversationId)
            throw new RemoteError("not_found", "消息不存在");
        return new JsonObject { ["message"] = RemoteProjection.Message(row, RemoteProjection.Detail.Full) };
    }

    /// <summary>Newest first until the budget is spent, then previews for the rest.</summary>
    private static (JsonArray Messages, bool HasOlder) Page(IReadOnlyList<MessageRow> rows, int end, int limit)
    {
        var start = Math.Max(0, end - limit);
        var items = new List<JsonObject>();
        var used = 0;
        for (var i = end - 1; i >= start; i--)
        {
            var detail = used > RemoteProjection.SnapshotBudget ? RemoteProjection.Detail.Compact : RemoteProjection.Detail.Normal;
            var item = RemoteProjection.Message(rows[i], detail);
            used += RemoteProjection.Size(item);
            items.Add(item);
        }
        items.Reverse();
        return (new JsonArray(items.Select(item => (JsonNode)item).ToArray()), start > 0);
    }

    private async Task<ConversationInfo> DescribeAsync(string conversationId)
    {
        ConversationInfo? info = null;
        await _onUi(() =>
        {
            var stored = _chat.ReadStoredConversation(conversationId);
            var row = stored?.Row ?? _conversations.Get(conversationId);
            if (row is not { DeletedAt: null }) throw new RemoteError("not_found", "对话不存在");
            var readOnly = stored is null ? "对话模型不可用"
                : stored.Provider is not PiWorkProvider ? "此对话不支持手机端操作"
                : stored.IsAtmosphereMode ? "角色扮演对话仅支持在电脑端继续"
                : null;
            var busy = _composer.IsConversationBusy(conversationId);
            var settings = readOnly is null ? _composer.DescribeRemoteSettings(conversationId) : null;
            info = new ConversationInfo(new JsonObject
            {
                ["id"] = conversationId,
                ["title"] = TitleOf(row),
                ["modelLabel"] = settings?.Model.DisplayName ?? stored?.Model.DisplayName ?? row.ModelId,
                ["providerLabel"] = settings?.Provider.DisplayName ?? stored?.Provider.DisplayName,
                ["busy"] = busy,
                ["readOnly"] = readOnly,
                ["settings"] = SettingsJson(settings)
            }, busy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return info!;
    }

    private static string TitleOf(ConversationRow row) => string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title.Trim();

    private bool HasSubscribers(string conversationId)
    {
        lock (_gate) return _subscriptions.Values.Any(mine => mine.ContainsKey(conversationId));
    }

    private void Push(string conversationId, string ev, JsonNode data)
    {
        List<string> peers;
        lock (_gate)
            peers = _subscriptions.Where(p => p.Value.ContainsKey(conversationId)).Select(p => p.Key).ToList();
        foreach (var peer in peers) PushTo(peer, conversationId, ev, data.DeepClone());
    }

    private void PushTo(string peer, string conversationId, string ev, JsonNode data)
    {
        long seq;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(peer, out var mine) || !mine.TryGetValue(conversationId, out var subscription))
                return;
            if (subscription.Pending is { } pending)
            {
                pending.Add((ev, data));
                return;
            }
            seq = ++subscription.Seq;
        }
        SendToPeer(peer, new JsonObject
        {
            ["v"] = Protocol, ["ev"] = ev, ["conversationId"] = conversationId, ["seq"] = seq, ["data"] = data
        });
    }

    /// <summary>Messages written to conversations someone is watching, sent as they
    /// are stored. A reply being written is checkpointed about once a second, so
    /// that is how often the phone sees it grow.</summary>
    private async Task WritePumpAsync(CancellationToken ct)
    {
        var reader = _writes.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Several writes of one message since the last pass collapse into one read.
                var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.TryRead(out var write)) batch[write.MessageId] = write.ConversationId;
                foreach (var (messageId, conversationId) in batch)
                {
                    if (!HasSubscribers(conversationId)) continue;
                    try
                    {
                        if (_messages.Get(messageId) is { } row)
                            Push(conversationId, "upsert", RemoteProjection.Message(row));
                    }
                    catch (Exception ex) { Log($"project: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Republishes the list, and tells phones watching the conversation its
    /// title, which changes once after the first answer when it is generated.</summary>
    private void OnConversationChanged(string conversationId)
    {
        ScheduleList(ListDebounce);
        if (!HasSubscribers(conversationId)) return;
        try
        {
            if (_conversations.Get(conversationId) is { DeletedAt: null } row)
                Push(conversationId, "state", new JsonObject { ["title"] = TitleOf(row) });
        }
        catch (Exception ex) { Log($"title: {ex.Message}"); }
    }

    private void OnConversationRewritten(string conversationId)
    {
        if (HasSubscribers(conversationId)) Push(conversationId, "reset", new JsonObject());
        ScheduleList(ListDebounce);
    }

    /// <summary>
    /// Whether each watched or listed conversation is answering, checked once a
    /// second: phones are told when it changes, and the list is republished. Read
    /// from the composer rather than inferred from writes, which say nothing while
    /// a tool runs.
    /// </summary>
    private async Task StateTickAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            SweepUploads();
            if (State != RemoteControlState.Online) continue;

            HashSet<string> watched;
            lock (_gate)
                watched = _subscriptions.Values.SelectMany(mine => mine.Keys).Concat(_listed).ToHashSet(StringComparer.Ordinal);
            if (watched.Count == 0) continue;

            var now = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                await _onUi(() =>
                {
                    foreach (var id in watched) now[id] = _composer.IsConversationBusy(id);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"state: {ex.Message}");
                continue;
            }

            var listChanged = false;
            foreach (var (id, busy) in now)
            {
                bool changed;
                bool listed;
                lock (_gate)
                {
                    changed = !_busy.TryGetValue(id, out var before) || before != busy;
                    _busy[id] = busy;
                    listed = _listed.Contains(id);
                }
                if (!changed) continue;
                listChanged |= listed;
                if (HasSubscribers(id)) Push(id, "state", StateJson(id, busy));
            }
            if (listChanged) ScheduleList(TimeSpan.Zero);
        }
    }

    private JsonObject StateJson(string conversationId, bool busy) => new()
    {
        ["busy"] = busy,
        ["pendingApprovals"] = ApprovalsFor(conversationId).Count
    };

    // ---- operations -----------------------------------------------------------

    private Task<JsonObject> SendAsync(string pairId, JsonObject args) =>
        RunMessageAsync(pairId, RequiredConversation(args), args,
            (conversationId, text, attachments) => _composer.SendFromRemote(conversationId, text, attachments));

    /// <summary>A conversation the phone drafted, started with its first message. The
    /// phone names it, so a retry after a lost reply finds the journal entry and the
    /// conversation the first attempt made, rather than making a second one.</summary>
    private async Task<JsonObject> CreateAsync(string pairId, JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        if (!IsConversationId(conversationId)) throw new RemoteError("bad_request", "对话标识无效");
        var reply = await RunMessageAsync(pairId, conversationId, args,
            (id, text, attachments) => _composer.CreateFromRemote(
                id, Text(args, "providerId"), Text(args, "modelId"), Text(args, "personaId"),
                Bool(args, "network"), Bool(args, "thinking"), Text(args, "effort"), text, attachments)).ConfigureAwait(false);
        reply["conversationId"] = conversationId;
        ScheduleList(TimeSpan.Zero);
        return reply;
    }

    private static bool IsConversationId(string id) =>
        id.Length is > 5 and <= 64 && id.StartsWith("chat_", StringComparison.Ordinal)
        && id.All(c => c is '_' or >= '0' and <= '9' or >= 'a' and <= 'z');

    /// <summary>
    /// The settings a new conversation would start with on this desktop, for the model
    /// and persona the phone picked, and the personas it may pick from.
    /// </summary>
    private async Task<JsonObject> NewDefaultsAsync(JsonObject args)
    {
        JsonObject? reply = null;
        await _onUi(() =>
        {
            var personaId = Text(args, "personaId");
            var settings = _composer.DescribeNewRemoteConversation(Text(args, "providerId"), Text(args, "modelId"), personaId)
                           ?? throw new RemoteError("not_found", "该模型不能在电脑上新建对话");
            var personas = _chat.RemotePersonas();
            var picked = _chat.DraftConversation(string.Empty, settings.Provider.Id, settings.Model.Id, personaId)?.Persona;
            reply = new JsonObject
            {
                ["settings"] = SettingsJson(settings),
                ["personaId"] = picked?.Id,
                ["personas"] = new JsonArray(personas.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["preview"] = p.Preview
                }).ToArray())
            };
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return reply!;
    }

    /// <summary>Runs one message from a phone at most once per command id.</summary>
    private async Task<JsonObject> RunMessageAsync(
        string pairId,
        string conversationId,
        JsonObject args,
        Func<string, string, IReadOnlyList<Attachment>, RemoteSendOutcome> run)
    {
        var text = Text(args, "text") ?? string.Empty;
        var commandId = Text(args, "cmdId") ?? throw new RemoteError("bad_request", "缺少命令标识");
        if (text.Length > 100_000) throw new RemoteError("too_large", "消息长度超出限制");
        var (uploadIds, attachments) = StagedAttachments(pairId, conversationId, args);

        // The same command arriving again (a phone retrying after a dropped reply,
        // or after this desktop restarted) is answered from the journal, never run twice.
        if (_journal.Read(conversationId, commandId) is { } earlier)
        {
            return earlier.Succeeded switch
            {
                true => new JsonObject { ["status"] = "duplicate" },
                null => throw new RemoteError("interrupted", "电脑端处理已中断，重发前请检查对话记录"),
                false => throw new RemoteError("refused", earlier.Error ?? "消息未处理")
            };
        }
        _journal.Write(conversationId, commandId, new RelayCommandJournal.Entry(null, null));

        RemoteSendOutcome? outcome = null;
        try
        {
            await _onUi(() =>
            {
                outcome = run(conversationId, text, attachments);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (RemoteSendRefusedException refused)
        {
            _journal.Write(conversationId, commandId, new RelayCommandJournal.Entry(false, refused.Message));
            throw new RemoteError(refused.Code, refused.Message);
        }
        catch (Exception ex)
        {
            _journal.Write(conversationId, commandId, new RelayCommandJournal.Entry(false, ex.Message));
            throw;
        }
        _journal.Write(conversationId, commandId, new RelayCommandJournal.Entry(true, null));
        // Consumed only once sent: a refused message can go again with the same files.
        DropUploads(upload => uploadIds.Contains(upload.Id));
        TrackRemoteTurn(conversationId, outcome!.Completion);
        Log($"send {conversationId}: {outcome.Status}");
        return new JsonObject
        {
            ["status"] = outcome.Status == RemoteSendStatus.Started ? "started" : "steered",
            ["messageId"] = outcome.MessageId
        };
    }

    private void TrackRemoteTurn(string conversationId, Task completion)
    {
        lock (_gate) _remoteTurns[conversationId] = _remoteTurns.GetValueOrDefault(conversationId) + 1;
        completion.ContinueWith(_ =>
        {
            lock (_gate)
            {
                var left = _remoteTurns.GetValueOrDefault(conversationId) - 1;
                if (left <= 0) _remoteTurns.Remove(conversationId);
                else _remoteTurns[conversationId] = left;
            }
        }, TaskScheduler.Default);
    }

    private async Task<JsonObject> StopTurnAsync(JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var stopped = false;
        await _onUi(() =>
        {
            stopped = _composer.StopConversation(conversationId);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return new JsonObject { ["stopped"] = stopped };
    }

    // ---- attachments ----------------------------------------------------------

    /// <summary>
    /// A file a phone is sending in pieces, then, once checked, the attachment it
    /// became, held until a <c>send</c> uses it. Requests from one phone run one at a
    /// time, so only the sweep competes with them; it takes the same lock.
    /// </summary>
    private sealed class Upload
    {
        public required string Id { get; init; }
        public required string PairId { get; init; }
        public required string Peer { get; init; }
        public required string ConversationId { get; init; }
        /// <summary>The model and persona of a conversation still being drafted on the
        /// phone; the conversation does not exist until its first message.</summary>
        public JsonObject? Draft { get; init; }
        public required string Name { get; init; }
        public required long Size { get; init; }
        public required string Sha256 { get; init; }
        public required string PartPath { get; init; }
        public FileStream? Stream;
        public IncrementalHash? Hash;
        public long Received;
        public int NextIndex;
        public Attachment? Staged;
        public long TouchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<JsonObject> UploadBeginAsync(string peer, string pairId, JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var name = UploadName(Text(args, "name"));
        var mime = Text(args, "mime") ?? string.Empty;
        var size = Long(args, "size") ?? throw new RemoteError("bad_request", "缺少文件大小");
        var sha256 = Text(args, "sha256")?.ToLowerInvariant();
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new RemoteError("bad_request", "缺少文件摘要");
        if (size <= 0) throw new RemoteError("empty", $"{name} 是空文件");
        if (size > MaxUploadBytes)
            throw new RemoteError("too_large", $"{name} 超过 {MaxUploadBytes / 1024 / 1024} MB，无法从手机发送");

        // A conversation still being drafted is described by the model the phone picked.
        var draft = _conversations.Get(conversationId) is null
            ? new JsonObject
            {
                ["providerId"] = Text(args, "providerId"),
                ["modelId"] = Text(args, "modelId"),
                ["personaId"] = Text(args, "personaId")
            }
            : null;
        var settings = await DescribeForUploadAsync(conversationId, draft).ConfigureAwait(false);
        // What the name and type already decide is refused before the bytes travel.
        if (LooksLikeImage(mime, name))
        {
            if (!settings.AcceptsImages) throw new RemoteError("unsupported", "当前模型不支持图片，请切换到支持视觉的模型");
        }
        else if (AttachmentMime.ClassifyDocument(mime, name) == AttachmentDocumentKind.LegacyOffice)
        {
            throw new RemoteError("unsupported", $"{name} 是旧版 Office 格式，请另存为 .docx / .xlsx / .pptx 后发送");
        }

        var id = Guid.NewGuid().ToString("N");
        var upload = new Upload
        {
            Id = id,
            PairId = pairId,
            Peer = peer,
            ConversationId = conversationId,
            Draft = draft,
            Name = name,
            Size = size,
            Sha256 = sha256,
            PartPath = Path.Combine(_uploadDirectory, id + ".part")
        };
        lock (_gate)
        {
            if (_uploads.Values.Count(u => u.PairId == pairId) >= MaxUploadsPerPhone)
                throw new RemoteError("too_many", "待发送的附件过多，请先发送或移除部分附件");
            _uploads[id] = upload;
        }
        try
        {
            Directory.CreateDirectory(_uploadDirectory);
            lock (upload)
            {
                upload.Stream = new FileStream(upload.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                upload.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }
        }
        catch (Exception)
        {
            DropUploads(u => u == upload);
            throw;
        }
        return new JsonObject { ["uploadId"] = id, ["chunkSize"] = UploadChunkBytes };
    }

    private JsonObject UploadChunk(string pairId, JsonObject args)
    {
        var upload = FindUpload(pairId, args);
        var index = Int(args, "index") ?? throw new RemoteError("bad_request", "缺少分片序号");
        var data = Text(args, "data") ?? throw new RemoteError("bad_request", "缺少分片数据");
        lock (upload)
        {
            if (upload.Staged is not null) throw new RemoteError("bad_request", "上传已完成");
            if (upload.Stream is null || upload.Hash is null)
                throw new RemoteError("not_found", "上传已失效，请重新发送该文件");
            // A piece sent again after its answer was lost.
            if (index < upload.NextIndex) return new JsonObject { ["received"] = upload.Received };
            if (index > upload.NextIndex) throw new RemoteError("out_of_order", "分片顺序错误，请重新发送该文件");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new RemoteError("bad_request", "分片数据无效"); }
            var last = upload.Received + bytes.Length == upload.Size;
            if (bytes.Length == 0 || bytes.Length > UploadChunkBytes || (!last && bytes.Length != UploadChunkBytes)
                || upload.Received + bytes.Length > upload.Size)
                throw new RemoteError("integrity", "分片大小与文件不符，请重新发送该文件");

            upload.Stream.Write(bytes);
            upload.Hash.AppendData(bytes);
            upload.Received += bytes.Length;
            upload.NextIndex++;
            upload.TouchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new JsonObject { ["received"] = upload.Received };
        }
    }

    private async Task<JsonObject> UploadEndAsync(string pairId, JsonObject args)
    {
        var upload = FindUpload(pairId, args);
        if (upload.Staged is { } done) return StagedJson(upload, done);

        try
        {
            lock (upload)
            {
                if (upload.Stream is null || upload.Hash is null)
                    throw new RemoteError("not_found", "上传已失效，请重新发送该文件");
                upload.Stream.Dispose();
                upload.Stream = null;
                var digest = Convert.ToHexStringLower(upload.Hash.GetHashAndReset());
                upload.Hash.Dispose();
                upload.Hash = null;
                if (upload.Received != upload.Size || digest != upload.Sha256)
                    throw new RemoteError("integrity", "文件校验失败，请重新发送");
            }
            var bytes = await File.ReadAllBytesAsync(upload.PartPath).ConfigureAwait(false);
            TryDelete(upload.PartPath);

            var settings = await DescribeForUploadAsync(upload.ConversationId, upload.Draft).ConfigureAwait(false);

            // The same gate as the attach button; the refusals are worded for a phone.
            var isImage = AttachmentMime.SniffImageMime(bytes) is not null;
            if (isImage && !settings.AcceptsImages)
                throw new RemoteError("unsupported", "当前模型不支持图片，请切换到支持视觉的模型");
            if (!isImage)
            {
                var kind = AttachmentMime.ClassifyDocument(null, upload.Name, bytes);
                if (kind == AttachmentDocumentKind.LegacyOffice)
                    throw new RemoteError("unsupported", $"{upload.Name} 是旧版 Office 格式，请另存为 .docx / .xlsx / .pptx 后发送");
                if (kind == AttachmentDocumentKind.Opaque && !settings.AcceptsOpaqueFiles)
                    throw new RemoteError("unsupported", $"无法从 {upload.Name} 中提取文字，此类文件需在电脑上开启 Python 工具");
            }
            var capabilities = new AttachmentIntakeCapabilities(settings.AcceptsImages, settings.AcceptsOpaqueFiles);
            var intake = await Task.Run(() => AttachmentIntake.FromBytes(bytes, upload.Name, capabilities)).ConfigureAwait(false);
            var attachment = intake.Attachment ?? throw new RemoteError("unsupported", intake.Error ?? "无法添加该附件");

            lock (upload)
            {
                upload.Staged = attachment;
                upload.TouchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Log($"upload {attachment.Kind}: {bytes.Length} bytes");
            return StagedJson(upload, attachment);
        }
        catch
        {
            DropUploads(u => u == upload);
            throw;
        }
    }

    private async Task<RemoteConversationSettings> DescribeForUploadAsync(string conversationId, JsonObject? draft)
    {
        RemoteConversationSettings? settings = null;
        await _onUi(() =>
        {
            settings = draft is null
                ? _composer.DescribeRemoteSettings(conversationId)
                : _composer.DescribeNewRemoteConversation(Text(draft, "providerId"), Text(draft, "modelId"), Text(draft, "personaId"));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return settings ?? throw new RemoteError("unsupported", "此对话不支持手机端操作");
    }

    private static JsonObject StagedJson(Upload upload, Attachment attachment) => new()
    {
        ["attachmentId"] = upload.Id,
        ["kind"] = attachment.Kind == AttachmentKind.Image ? "image" : "file",
        ["name"] = upload.Name,
        ["size"] = attachment.Bytes.Length
    };

    private JsonObject UploadCancel(string pairId, JsonObject args)
    {
        var id = Text(args, "uploadId");
        DropUploads(upload => upload.Id == id && upload.PairId == pairId);
        return new JsonObject();
    }

    private Upload FindUpload(string pairId, JsonObject args)
    {
        var id = Text(args, "uploadId") ?? throw new RemoteError("bad_request", "缺少上传标识");
        lock (_gate)
        {
            if (_uploads.TryGetValue(id, out var upload) && upload.PairId == pairId) return upload;
        }
        throw new RemoteError("not_found", "上传已失效，请重新发送该文件");
    }

    /// <summary>The finished uploads a <c>send</c> names, in the order it names them.</summary>
    private (HashSet<string> Ids, List<Attachment> Attachments) StagedAttachments(
        string pairId, string conversationId, JsonObject args)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var attachments = new List<Attachment>();
        if (args["attachments"] is not JsonArray list) return (ids, attachments);
        if (list.Count > MaxAttachmentsPerMessage) throw new RemoteError("too_many", "附件数量超出限制");
        lock (_gate)
        {
            foreach (var item in list)
            {
                var id = item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (id is null || !ids.Add(id)) continue;
                if (!_uploads.TryGetValue(id, out var upload) || upload.PairId != pairId
                    || upload.ConversationId != conversationId || upload.Staged is not { } attachment)
                    throw new RemoteError("not_found", "附件已失效，请重新添加");
                attachments.Add(attachment);
            }
        }
        return (ids, attachments);
    }

    /// <summary>Stalled uploads and attachments never sent are let go.</summary>
    private void SweepUploads()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        DropUploads(upload => now - upload.TouchedAtMs >
            (upload.Staged is null ? UploadIdle : StagedLifetime).TotalMilliseconds);
    }

    private void DropUploads(Func<Upload, bool> which)
    {
        List<Upload> dropped;
        lock (_gate)
        {
            if (_uploads.Count == 0) return;
            dropped = _uploads.Values.Where(which).ToList();
            foreach (var upload in dropped) _uploads.Remove(upload.Id);
        }
        foreach (var upload in dropped)
        {
            lock (upload)
            {
                upload.Stream?.Dispose();
                upload.Stream = null;
                upload.Hash?.Dispose();
                upload.Hash = null;
            }
            TryDelete(upload.PartPath);
        }
    }

    /// <summary>Pieces left behind by a crash or a quit mid-upload.</summary>
    private void ClearUploadDirectory()
    {
        try
        {
            if (!Directory.Exists(_uploadDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(_uploadDirectory, "*.part")) TryDelete(file);
        }
        catch (Exception) { /* Swept again on the next start. */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* A leftover piece is cleared on the next start. */ }
    }

    private static string UploadName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(Path.GetFileName((name ?? string.Empty).Replace('\\', '/'))
            .Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray()).Trim();
        if (safe.Length == 0) safe = $"附件_{DateTime.Now:HHmmss}";
        return safe.Length <= 120 ? safe : safe[..120];
    }

    private static bool LooksLikeImage(string mime, string name) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"
            or ".heic" or ".heif" or ".avif" or ".tif" or ".tiff";

    // ---- model and switches ---------------------------------------------------

    private async Task<JsonObject> ModelsAsync()
    {
        var groups = new JsonArray();
        await _onUi(() =>
        {
            foreach (var group in _composer.RemoteModelChoices().GroupBy(choice => choice.Provider.Id))
            {
                var provider = group.First().Provider;
                groups.Add(new JsonObject
                {
                    ["providerId"] = provider.Id,
                    ["provider"] = provider.DisplayName,
                    ["family"] = Family(provider),
                    ["models"] = new JsonArray(group.Select(choice => (JsonNode)new JsonObject
                    {
                        ["id"] = choice.Model.Id,
                        ["label"] = choice.Model.DisplayName
                    }).ToArray())
                });
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return new JsonObject { ["groups"] = groups };
    }

    private async Task<JsonObject> ConfigureAsync(JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        JsonObject? settings = null;
        try
        {
            await _onUi(() =>
            {
                _composer.ConfigureFromRemote(
                    conversationId, Text(args, "providerId"), Text(args, "modelId"),
                    Bool(args, "network"), Bool(args, "thinking"), Text(args, "effort"));
                settings = SettingsJson(_composer.DescribeRemoteSettings(conversationId));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (RemoteSendRefusedException refused)
        {
            throw new RemoteError(refused.Code, refused.Message);
        }
        if (settings is not null) Push(conversationId, "settings", settings.DeepClone());
        ScheduleList(ListDebounce);
        return new JsonObject { ["settings"] = settings };
    }

    /// <summary>Runs on the UI thread, where the composer raised the change.</summary>
    private void PushSettingsOnScreen()
    {
        if (_chat.ConversationId is not { } conversationId || !HasSubscribers(conversationId)) return;
        try
        {
            if (SettingsJson(_composer.DescribeRemoteSettings(conversationId)) is { } settings)
                Push(conversationId, "settings", settings);
        }
        catch (Exception ex) { Log($"settings: {ex.Message}"); }
    }

    private static JsonObject? SettingsJson(RemoteConversationSettings? settings) => settings is null ? null : new JsonObject
    {
        ["providerId"] = settings.Provider.Id,
        ["modelId"] = settings.Model.Id,
        ["model"] = settings.Model.DisplayName,
        ["provider"] = settings.Provider.DisplayName,
        ["family"] = Family(settings.Provider),
        ["network"] = new JsonObject { ["available"] = settings.NetworkAvailable, ["on"] = settings.NetworkOn },
        ["thinking"] = new JsonObject
        {
            ["toggle"] = settings.ThinkingToggle,
            ["on"] = settings.ThinkingOn,
            ["levels"] = new JsonArray(settings.EffortLevels.Select(level => (JsonNode?)JsonValue.Create(level)).ToArray()),
            ["effort"] = settings.Effort
        },
        ["attachments"] = new JsonObject
        {
            ["images"] = settings.AcceptsImages,
            ["files"] = settings.AcceptsOpaqueFiles
        }
    };

    private static string Family(IChatProvider provider) =>
        provider.ToAppMode() == AppMode.Work ? "work" : "byok";

    // ---- approvals ------------------------------------------------------------

    private sealed class PendingApproval(RemoteControlHost host, RemoteApprovalPrompt prompt) : IRemoteApprovalTicket
    {
        public RemoteApprovalPrompt Prompt { get; } = prompt;
        public TaskCompletionSource<string> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Answer => Tcs.Task;
        public void SettleOnDesktop(string decision) => host.Resolve(this, decision, "desktop");
        public void Abandon() => host.Resolve(this, RemoteApprovalDecisions.Deny, "cancelled");
    }

    public IRemoteApprovalTicket? Offer(RemoteApprovalPrompt prompt, bool desktopVisible)
    {
        if (State != RemoteControlState.Online || !_phones.Any()) return null;
        bool wanted;
        lock (_gate) wanted = !desktopVisible || _remoteTurns.ContainsKey(prompt.ConversationId);
        wanted |= HasSubscribers(prompt.ConversationId);
        if (!wanted) return null;

        var pending = new PendingApproval(this, prompt);
        lock (_gate) _approvals[prompt.RequestId] = pending;
        Log($"approval offered {prompt.ToolName}");
        Push(prompt.ConversationId, "approval", PromptJson(prompt));
        Push(prompt.ConversationId, "state", StateJson(prompt.ConversationId, true));
        ScheduleList(TimeSpan.Zero);
        return pending;
    }

    private JsonObject Approve(JsonObject args)
    {
        var requestId = Text(args, "requestId") ?? throw new RemoteError("bad_request", "缺少审批标识");
        var decision = Text(args, "decision");
        if (decision is not (RemoteApprovalDecisions.Once or RemoteApprovalDecisions.Session or RemoteApprovalDecisions.Deny))
            throw new RemoteError("bad_request", "审批选项无效");
        PendingApproval? pending;
        lock (_gate) _approvals.TryGetValue(requestId, out pending);
        if (pending is null) throw new RemoteError("resolved", "审批已处理");
        if (!pending.Prompt.Options.Contains(decision))
            throw new RemoteError("bad_request", "不支持此审批选项");
        Resolve(pending, decision, "phone");
        return new JsonObject();
    }

    private void Resolve(PendingApproval pending, string decision, string by)
    {
        lock (_gate)
        {
            if (!_approvals.Remove(pending.Prompt.RequestId)) return;
        }
        pending.Tcs.TrySetResult(decision);
        Log($"approval {decision} by {by}");
        var conversationId = pending.Prompt.ConversationId;
        Push(conversationId, "resolved", new JsonObject
        {
            ["requestId"] = pending.Prompt.RequestId, ["decision"] = decision, ["by"] = by
        });
        bool busy;
        lock (_gate) busy = _busy.GetValueOrDefault(conversationId);
        Push(conversationId, "state", StateJson(conversationId, busy));
        ScheduleList(TimeSpan.Zero);
    }

    private List<PendingApproval> ApprovalsFor(string conversationId)
    {
        lock (_gate) return _approvals.Values.Where(p => p.Prompt.ConversationId == conversationId).ToList();
    }

    private List<PendingApproval> TakeAllApprovals()
    {
        lock (_gate)
        {
            var all = _approvals.Values.ToList();
            _approvals.Clear();
            return all;
        }
    }

    private static JsonObject PromptJson(RemoteApprovalPrompt prompt) => new()
    {
        ["requestId"] = prompt.RequestId,
        ["conversationId"] = prompt.ConversationId,
        ["kind"] = prompt.Kind,
        ["toolName"] = prompt.ToolName,
        ["displayName"] = prompt.DisplayName,
        ["summary"] = prompt.Summary,
        ["argumentsPreview"] = prompt.ArgumentsPreview,
        ["codePreview"] = prompt.CodePreview,
        ["resolvedPath"] = prompt.ResolvedPath,
        ["capabilities"] = new JsonArray(prompt.Capabilities.Select(c => (JsonNode)c).ToArray()),
        ["options"] = new JsonArray(prompt.Options.Select(o => (JsonNode)o).ToArray()),
        ["createdAtMs"] = prompt.CreatedAtMs
    };

    // ---- conversation list ----------------------------------------------------

    private void ScheduleList(TimeSpan delay)
    {
        var due = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds();
        long current;
        do
        {
            current = Interlocked.Read(ref _listDueAtMs);
            if (current != 0 && current <= due) return;
        } while (Interlocked.CompareExchange(ref _listDueAtMs, due, current) != current);
        // One pending wake is enough; the loop reads the due time when it runs.
        if (_listSignal.CurrentCount == 0) _listSignal.Release();
    }

    private async Task DirectoryLoopAsync(CancellationToken ct)
    {
        var lastContact = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var due = Interlocked.Read(ref _listDueAtMs);
                var now = DateTimeOffset.UtcNow;
                if (due != 0 && due <= now.ToUnixTimeMilliseconds())
                {
                    Interlocked.CompareExchange(ref _listDueAtMs, 0, due);
                    await PublishListAsync(ct).ConfigureAwait(false);
                    lastContact = DateTimeOffset.UtcNow;
                }
                else if (now - lastContact >= PingInterval)
                {
                    using var reply = await PostDirectoryAsync(
                        new JsonObject { ["kind"] = "ping", ["machineId"] = _machineId }, ct).ConfigureAwait(false);
                    // The phone removed this computer, or the record expired: publish again.
                    if (reply.RootElement.TryGetProperty("known", out var known) && known.ValueKind == JsonValueKind.False)
                    {
                        _lastPublishedList = null;
                        ScheduleList(TimeSpan.Zero);
                    }
                    lastContact = now;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log($"directory: {ex.Message}");
                _lastPublishedList = null;
                ScheduleList(TimeSpan.FromSeconds(10));
            }

            var wait = TimeSpan.FromSeconds(5);
            var dueAt = Interlocked.Read(ref _listDueAtMs);
            if (dueAt != 0)
                wait = TimeSpan.FromMilliseconds(Math.Clamp(dueAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 5000));
            try { await _listSignal.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishListAsync(CancellationToken ct)
    {
        var rows = await Task.Run(() => _conversations.ListActive(), ct).ConfigureAwait(false);
        var conversations = new JsonArray();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await _onUi(() =>
        {
            foreach (var row in rows.OrderByDescending(r => r.UpdatedAt))
            {
                if (conversations.Count >= ListLimit) break;
                if (_providers.GetById(row.ProviderId ?? string.Empty) is not PiWorkProvider provider) continue;
                var busy = _composer.IsConversationBusy(row.Id);
                conversations.Add(new JsonObject
                {
                    ["id"] = row.Id,
                    ["title"] = TitleOf(row),
                    ["modelLabel"] = provider.Models.FirstOrDefault(m => m.Id == row.ModelId)?.DisplayName ?? row.ModelId,
                    ["providerLabel"] = provider.DisplayName,
                    ["updatedAtMs"] = row.UpdatedAt,
                    ["busy"] = busy,
                    ["pendingApprovals"] = ApprovalsFor(row.Id).Count
                });
                listed.Add(row.Id);
                lock (_gate) _busy[row.Id] = busy;
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        lock (_gate) _listed = listed;

        var body = new JsonObject
        {
            ["kind"] = "put",
            ["machineId"] = _machineId,
            ["machineName"] = _machineName,
            ["appVersion"] = _appVersion,
            ["protocol"] = Protocol,
            ["conversations"] = conversations
        };
        var json = body.ToJsonString();
        if (json == _lastPublishedList)
        {
            using var _ = await PostDirectoryAsync(new JsonObject { ["kind"] = "ping", ["machineId"] = _machineId }, ct).ConfigureAwait(false);
            return;
        }
        using (await PostDirectoryAsync(body, ct).ConfigureAwait(false)) { }
        _lastPublishedList = json;
    }

    private async Task<JsonDocument> PostDirectoryAsync(JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/auth/agent_desktops.php")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AttachAuth(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // ---- helpers --------------------------------------------------------------

    private void AttachAuth(HttpRequestMessage request)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) throw new InvalidOperationException("Not signed in.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    }

    private static JsonObject ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { throw new RemoteError("bad_request", "请求参数无效"); }
    }

    private static string RequiredConversation(JsonObject args) =>
        Text(args, "conversationId") is { Length: > 0 and <= 128 } id ? id : throw new RemoteError("bad_request", "缺少对话标识");

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Long(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static int? Int(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? Bool(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static void PruneJournal(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (Exception) { /* A leftover entry only costs a few bytes. */ }
    }

    private void Log(string line) => _log?.Invoke(line);
}
