using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

public sealed class CloudSyncService
{
    private const string Endpoint = "https://chatgpt.wljay.cn/v2/api/auth/sync.php";
    private const string TitleEndpoint = "https://chatgpt.wljay.cn/v2/api/auth/generateTitle.php";
    private const string LastSyncKey = "cloud_sync.last_sync_timestamp";
    private const string SyncEnabledKey = "sync_conversations";
    private const string BoundAccountKey = "cloud_sync.bound_account";
    private const string DetailRefreshPrefix = "cloud_sync.detail_refresh.";
    private const string DetailParserVersionPrefix = "cloud_sync.detail_parser_version.";
    private const string DetailParserVersion = "2026-06-12.local-timeline-offsets-v2";
    private const string ConversationSyncPrefix = "cloud_sync.conversation_timestamp.";
    private const string ConversationMetadataPrefix = "cloud_sync.metadata.";
    private const string TitleGeneratedPrefix = "cloud_sync.ai_title_generated.";
    private const string MolaGptProviderId = "molagpt-proxy";
    private const string EpochIso = "1970-01-01T00:00:00.000Z";
    private const int ChunkSize = 5 * 1024 * 1024;
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(15);
    // Single-conversation detail fetch (opening a conversation). The shared
    // HttpClient has Timeout.InfiniteTimeSpan, so without this an unreachable
    // network leaves the request — and the "正在修复对话显示..." status —
    // hung forever. Bounded so a bad network fails fast and resets state.
    private static readonly TimeSpan DetailFetchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromMinutes(3);

    // .NET 8 marks JsonSerializerOptions read-only on first use through
    // JsonContent.Create. If TypeInfoResolver hasn't been set explicitly
    // by then, every subsequent JsonContent.Create call throws
    // "JsonSerializerOptions instance must specify a TypeInfoResolver
    // setting before being marked as read-only". The reflection-based
    // resolver is fine for our use (no AOT requirements here).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly HttpClient _http;
    private readonly MolaGptAuthService _auth;
    private readonly ConversationRepository _conversations;
    private readonly MessageRepository _messages;
    private readonly SettingsRepository _settings;
    private int _isSyncing;
    private CancellationTokenSource? _periodicSyncCts;

    public event EventHandler<CloudSyncStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? LocalConversationsChanged;
    public bool IsSyncing => Volatile.Read(ref _isSyncing) == 1;

    public CloudSyncService(
        HttpClient http,
        MolaGptAuthService auth,
        ConversationRepository conversations,
        MessageRepository messages,
        SettingsRepository settings)
    {
        _http = http;
        _auth = auth;
        _conversations = conversations;
        _messages = messages;
        _settings = settings;
    }

    public async Task<CloudSyncResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool publishStatus = true)
    {
        if (Interlocked.Exchange(ref _isSyncing, 1) == 1)
            throw new InvalidOperationException("云同步正在进行中。");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SyncTimeout);
        var token = timeoutCts.Token;
        void Report(string message)
        {
            progress?.Report(message);
            if (publishStatus)
                PublishStatus(CloudSyncState.Syncing, message);
        }

        try
        {
            if (publishStatus)
                PublishStatus(CloudSyncState.Syncing, "正在同步对话...");
            var jwt = _auth.CurrentJwt;
            if (string.IsNullOrWhiteSpace(jwt))
                throw new InvalidOperationException("请先登录 MolaGPT 账号。");

            if (bool.TryParse(_settings.Get(SyncEnabledKey), out var enabled) && !enabled)
                throw new InvalidOperationException("请先开启对话数据同步。");

            // Account-binding guard (cross-account leak prevention): if the
            // local MolaGPT data is bound to a different account than the one
            // now logged in, purge the previous account's local conversations
            // and reset sync state BEFORE building the dirty set. Otherwise a
            // first sync would upload the previous account's retained
            // conversations to the new account.
            var accountBinding = await Task.Run(() => EnforceAccountBinding(), token).ConfigureAwait(false);

            var lastSync = _settings.Get(LastSyncKey) ?? EpochIso;
            var lastSyncMs = ToUnixMilliseconds(lastSync);
            var isFirstSync = string.Equals(lastSync, EpochIso, StringComparison.Ordinal);

            Report("正在同步删除状态...");
            var deletedIds = await Task.Run(() => _conversations.ListDeletedSince(lastSyncMs)
                    .Where(IsCloudSyncable)
                    .Select(row => row.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(), token)
                .ConfigureAwait(false);

            if (deletedIds.Length > 0)
                await DeleteCloudConversationsAsync(jwt, deletedIds, token).ConfigureAwait(false);

            Report("正在整理本地对话...");
            var dirty = await Task.Run(() => BuildDirtyConversations(lastSyncMs, isFirstSync), token)
                .ConfigureAwait(false);
            Report(dirty.Count > 0
                ? $"正在上传 {dirty.Count} 个本地更新..."
                : "正在拉取云端对话列表...");

            var syncResult = await PostSyncAsync(jwt, new JsonObject
            {
                ["action"] = "full_sync",
                ["last_sync_timestamp"] = lastSync,
                ["dirty_conversations"] = dirty
            }, token, progress).ConfigureAwait(false);

            if (dirty.Count > 0 && !HasServerMetadata(syncResult))
            {
                Report("正在刷新云端对话列表...");
                var refreshTimestamp = syncResult["new_sync_timestamp"]?.GetValue<string>() ?? lastSync;
                syncResult = await PostSyncAsync(jwt, new JsonObject
                {
                    ["action"] = "full_sync",
                    ["last_sync_timestamp"] = refreshTimestamp,
                    ["dirty_conversations"] = new JsonArray()
                }, token, progress).ConfigureAwait(false);
            }

            var syncTimestamp = syncResult["new_sync_timestamp"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("O");
            Report("正在合并对话列表...");
            var merge = await Task.Run(() =>
            {
                var result = MergeServerMetadata(syncResult, syncTimestamp);
                _settings.Set(LastSyncKey, syncTimestamp);
                var uploadedRows = MarkUploadedConversations(dirty, syncTimestamp);
                return result with { ChangedRows = result.ChangedRows.Concat(uploadedRows).ToList() };
            }, token).ConfigureAwait(false);

            if (publishStatus)
                PublishStatus(CloudSyncState.Success, "同步成功");
            return new CloudSyncResult(
                Uploaded: dirty.Count,
                Downloaded: merge.Upserted,
                Deleted: accountBinding.PurgedConversationCount + deletedIds.Length + merge.RemoteDeleted,
                LastSyncTimestamp: syncTimestamp,
                ChangedRows: merge.ChangedRows,
                RemovedIds: accountBinding.PurgedIds
                    .Concat(deletedIds)
                    .Concat(merge.RemovedIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                RequiresFullReload: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (publishStatus)
                PublishStatus(CloudSyncState.Error, "云同步超时");
            throw new TimeoutException("云同步超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            if (publishStatus)
                PublishStatus(CloudSyncState.Error, $"同步失败：{ex.Message}");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    public void StartPeriodicSync()
    {
        if (_periodicSyncCts is not null) return;
        _periodicSyncCts = new CancellationTokenSource();
        _ = RunPeriodicSyncAsync(_periodicSyncCts.Token);
    }

    public void StopPeriodicSync()
    {
        var cts = _periodicSyncCts;
        _periodicSyncCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public async Task<CloudSyncResult?> RequestForegroundSyncAsync(CancellationToken ct = default)
    {
        return await TryPeriodicSyncAsync(ct, publishStatus: true).ConfigureAwait(false);
    }

    private async Task RunPeriodicSyncAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(PeriodicSyncInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await TryPeriodicSyncAsync(ct, publishStatus: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<CloudSyncResult?> TryPeriodicSyncAsync(CancellationToken ct, bool publishStatus)
    {
        if (string.IsNullOrWhiteSpace(_auth.CurrentJwt)) return null;
        if (bool.TryParse(_settings.Get(SyncEnabledKey), out var enabled) && !enabled)
        {
            return null;
        }

        try
        {
            return await SyncAsync(null, ct, publishStatus).ConfigureAwait(false);
        }
        catch
        {
            // The status event already carries foreground failures; periodic failures
            // should not interrupt the user flow.
            return null;
        }
    }

    public async Task UpdateCloudSyncSettingAsync(bool enabled, CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt)) return;

        await PostSyncAsync(jwt, new JsonObject
        {
            ["action"] = "update_setting",
            ["setting"] = "cloud_sync_enabled",
            ["value"] = enabled
        }, ct).ConfigureAwait(false);

        PublishStatus(enabled ? CloudSyncState.Idle : CloudSyncState.Disabled,
            enabled ? "云同步已开启" : "云同步已关闭");
    }

    public async Task<bool> FetchConversationToLocalAsync(string conversationId, CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt)) return false;

        var accountBinding = await Task.Run(() => EnforceAccountBinding(), ct).ConfigureAwait(false);
        if (accountBinding.PurgedConversationCount > 0) return false;

        var refreshKey = DetailRefreshKey(conversationId);
        var needsRefresh = !string.IsNullOrWhiteSpace(_settings.Get(refreshKey));
        var isSyncedConversation = HasConversationSyncTimestamp(conversationId);
        var needsParserRefresh = isSyncedConversation
                                 && _settings.Get(DetailParserVersionKey(conversationId)) != DetailParserVersion;
        var existingMessages = await Task.Run(() => _messages.List(conversationId), ct)
            .ConfigureAwait(false);
        if (existingMessages.Count > 0 && !needsRefresh && !needsParserRefresh) return false;

        JsonObject? detail;
        using var detailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        detailCts.CancelAfter(DetailFetchTimeout);
        try
        {
            PublishStatus(CloudSyncState.Syncing, needsParserRefresh ? "正在修复对话显示..." : "正在拉取对话...");
            detail = await FetchConversationAsync(jwt, conversationId, detailCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled (e.g. switched to another conversation). Stay
            // quiet; the new load drives its own status.
            return false;
        }
        catch (OperationCanceledException)
        {
            // Our own DetailFetchTimeout fired — the network is unreachable or
            // too slow. Surface a recoverable error instead of hanging on the
            // "正在修复对话显示..." spinner forever.
            PublishStatus(CloudSyncState.Error, "网络不稳定，对话加载超时，请稍后重试。");
            return false;
        }
        catch (Exception ex)
        {
            PublishStatus(CloudSyncState.Error, $"拉取对话失败：{ex.Message}");
            return false;
        }

        if (detail is null || detail["messages"] is not JsonArray messages || messages.Count == 0)
            return false;

        var metadata = detail["metadata"] as JsonObject;
        if (metadata is not null)
        {
            var existing = _conversations.Get(conversationId);
            _conversations.Upsert(ToLocalConversation(metadata, existing));
            SaveCloudMetadata(conversationId, metadata);
            var metadataUpdated = metadata["updated_at"]?.GetValue<string>()
                                  ?? metadata["time"]?.GetValue<string>()
                                  ?? DateTimeOffset.UtcNow.ToString("O");
            SetConversationSyncTimestamp(conversationId, metadataUpdated);
        }

        _messages.ReplaceConversationMessages(conversationId, ToLocalMessages(conversationId, messages));
        _settings.Remove(refreshKey);
        _settings.Set(DetailParserVersionKey(conversationId), DetailParserVersion);
        PublishStatus(CloudSyncState.Success, "对话已更新");
        return true;
    }

    public async Task<string?> CompleteConversationTurnAsync(string conversationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return null;

        var accountBinding = await Task.Run(() => EnforceAccountBinding(), ct).ConfigureAwait(false);
        if (accountBinding.PurgedConversationCount > 0) return null;

        var row = await Task.Run(() => _conversations.Get(conversationId), ct)
            .ConfigureAwait(false);
        if (row is null || !IsCloudSyncable(row)) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PushTimeout);
        var token = timeoutCts.Token;

        string? generatedTitle = null;
        using var titleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        titleCts.CancelAfter(TitleTimeout);
        try
        {
            generatedTitle = await TryGenerateAiTitleAsync(conversationId, titleCts.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            generatedTitle = null;
        }

        try
        {
            await PushSingleConversationAsync(conversationId, token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Fire-and-forget incremental sync: manual sync remains available.
        }

        return generatedTitle;
    }

    public async Task<bool> PushSingleConversationAsync(string conversationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        while (Interlocked.Exchange(ref _isSyncing, 1) == 1)
            await Task.Delay(1000, ct).ConfigureAwait(false);

        try
        {
            var jwt = _auth.CurrentJwt;
            if (string.IsNullOrWhiteSpace(jwt)) return false;
            if (bool.TryParse(_settings.Get(SyncEnabledKey), out var enabled) && !enabled) return false;

            var accountBinding = await Task.Run(() => EnforceAccountBinding(), ct).ConfigureAwait(false);
            if (accountBinding.PurgedConversationCount > 0) return false;

            var snapshot = await Task.Run(() =>
            {
                var conversation = _conversations.Get(conversationId);
                var messages = conversation is null ? Array.Empty<MessageRow>() : _messages.List(conversationId);
                return (conversation, messages);
            }, ct).ConfigureAwait(false);

            var row = snapshot.conversation;
            if (row is null || row.DeletedAt is not null) return false;
            if (!IsCloudSyncable(row)) return false;

            var rows = snapshot.messages;
            if (rows.Count == 0) return false;

            PublishStatus(CloudSyncState.Syncing, "正在同步当前对话...");
            var uploadRow = EnsureGeneratedTitle(row, rows);
            var dirty = new JsonArray
            {
                new JsonObject
                {
                    ["metadata"] = ToCloudMetadata(uploadRow),
                    ["messages"] = ToCloudMessages(rows)
                }
            };

            var result = await PostSyncAsync(jwt, new JsonObject
            {
                ["action"] = "full_sync",
                ["last_sync_timestamp"] = _settings.Get(LastSyncKey) ?? EpochIso,
                ["dirty_conversations"] = dirty
            }, ct).ConfigureAwait(false);

            var syncTimestamp = result["new_sync_timestamp"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("O");
            SetConversationSyncTimestamp(conversationId, syncTimestamp);
            PublishStatus(CloudSyncState.Success, "当前对话已同步");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PublishStatus(CloudSyncState.Error, $"当前对话同步失败：{ex.Message}");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    public async Task PushDeletedConversationsAsync(IReadOnlyList<string> conversationIds, CancellationToken ct = default)
    {
        if (conversationIds.Count == 0) return;
        if (string.IsNullOrWhiteSpace(_auth.CurrentJwt))
        {
            ForgetLoggedOutLocalDeletes(conversationIds);
            return;
        }

        while (Interlocked.Exchange(ref _isSyncing, 1) == 1)
            await Task.Delay(1000, ct).ConfigureAwait(false);

        try
        {
            var jwt = _auth.CurrentJwt;
            if (string.IsNullOrWhiteSpace(jwt))
            {
                ForgetLoggedOutLocalDeletes(conversationIds);
                return;
            }
            if (bool.TryParse(_settings.Get(SyncEnabledKey), out var enabled) && !enabled) return;

            var accountBinding = await Task.Run(() => EnforceAccountBinding(), ct).ConfigureAwait(false);
            if (accountBinding.PurgedConversationCount > 0) return;

            var ids = conversationIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0) return;

            PublishStatus(CloudSyncState.Syncing, "正在同步删除...");
            await DeleteCloudConversationsAsync(jwt, ids, ct).ConfigureAwait(false);
            foreach (var id in ids)
            {
                _settings.Remove(ConversationSyncKey(id));
                _settings.Remove(CloudMetadataKey(id));
                _settings.Remove(DetailRefreshKey(id));
                _settings.Remove(DetailParserVersionKey(id));
                _settings.Remove(TitleGeneratedKey(id));
            }
            PublishStatus(CloudSyncState.Success, "删除已同步");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PublishStatus(CloudSyncState.Error, $"删除同步失败：{ex.Message}");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isSyncing, 0);
        }
    }

    private void ForgetLoggedOutLocalDeletes(IReadOnlyList<string> conversationIds)
    {
        var deletedIds = _conversations.HardDeleteDeletedByProvider(conversationIds, MolaGptProviderId);
        foreach (var id in deletedIds)
        {
            _settings.Remove(ConversationSyncKey(id));
            _settings.Remove(CloudMetadataKey(id));
            _settings.Remove(DetailRefreshKey(id));
            _settings.Remove(DetailParserVersionKey(id));
            _settings.Remove(TitleGeneratedKey(id));
        }
    }

    private async Task<string?> TryGenerateAiTitleAsync(string conversationId, CancellationToken ct)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        if (!string.IsNullOrWhiteSpace(_settings.Get(TitleGeneratedKey(conversationId)))) return null;

        var row = _conversations.Get(conversationId);
        if (row is null || row.DeletedAt is not null) return null;
        if (!IsCloudSyncable(row)) return null;

        var rows = _messages.List(conversationId);
        if (rows.Count < 2 || rows.Count > 2) return null;

        var user = rows.FirstOrDefault(message => message.Role == "user");
        var assistant = rows.FirstOrDefault(message => message.Role == "assistant");
        if (string.IsNullOrWhiteSpace(user?.Content) || string.IsNullOrWhiteSpace(assistant?.Content))
            return null;

        var assistantPreview = assistant.Content.Length <= 1000
            ? assistant.Content
            : assistant.Content[..1000];

        var endpoint = NetworkSecurity.RequireHttps(new Uri(TitleEndpoint), "MolaGPT 标题生成");
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是一个专门生成对话标题的助手。请根据用户的问题和AI的回答，请根据对话内容生成中文标题，必须极度简短，标点符号省略，最长不得超过14个字符。只返回标题本身，不要有任何其他内容、标点符号、引号或解释。"
                    },
                    new
                    {
                        role = "user",
                        content = $"用户问题：{user.Content}\n\nAI回答：{assistantPreview}\n\n请为这段对话生成一个简洁的标题（不超过14个字）："
                    }
                },
                temperature = 0.2,
                max_tokens = 30
            }, options: JsonOptions)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var result = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        if (result is null || result["success"]?.GetValue<bool>() == false) return null;

        var title = ExtractGeneratedTitle(result);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _conversations.Upsert(row with
        {
            Title = title,
            UpdatedAt = Math.Max(row.UpdatedAt, now)
        });
        _settings.Set(TitleGeneratedKey(conversationId), "true");
        return title;
    }

    public int CleanupLocalPlaceholdersForLogout()
    {
        var deletedIds = _conversations.HardDeleteEmptyByProvider(MolaGptProviderId);
        foreach (var id in deletedIds)
        {
            _settings.Remove(ConversationSyncKey(id));
            _settings.Remove(CloudMetadataKey(id));
            _settings.Remove(DetailRefreshKey(id));
            _settings.Remove(DetailParserVersionKey(id));
            _settings.Remove(TitleGeneratedKey(id));
        }

        if (deletedIds.Count > 0)
            LocalConversationsChanged?.Invoke(this, EventArgs.Empty);

        return deletedIds.Count;
    }

    /// <summary>
    /// Ensures local MolaGPT data belongs to the currently logged-in account.
    /// If the stored binding names a different account, the previous account's
    /// local conversations and all sync bookkeeping are purged so the upcoming
    /// sync starts clean for the new account. The binding is written immediately
    /// after adopting or purging. No-op when the account matches or no username
    /// is available yet (e.g. token applied but profile not fetched).
    /// </summary>
    private AccountBindingResult EnforceAccountBinding()
    {
        var current = _auth.CurrentUsername;
        if (string.IsNullOrWhiteSpace(current)) return AccountBindingResult.Empty;

        var bound = _settings.Get(BoundAccountKey);
        if (string.IsNullOrWhiteSpace(bound))
        {
            // First time binding (or upgraded from a pre-binding build): adopt
            // the current account without purging. Existing local data is
            // assumed to belong to this account.
            _settings.Set(BoundAccountKey, current);
            return AccountBindingResult.Empty;
        }

        if (string.Equals(bound, current, StringComparison.Ordinal)) return AccountBindingResult.Empty;

        // Different account: purge previous account's local MolaGPT data.
        var purged = _conversations.PurgeAllByProvider(MolaGptProviderId);
        foreach (var id in purged)
        {
            _settings.Remove(ConversationSyncKey(id));
            _settings.Remove(CloudMetadataKey(id));
            _settings.Remove(DetailRefreshKey(id));
            _settings.Remove(DetailParserVersionKey(id));
            _settings.Remove(TitleGeneratedKey(id));
        }
        _settings.Remove(LastSyncKey);
        _settings.Set(BoundAccountKey, current);
        if (purged.Count > 0)
            LocalConversationsChanged?.Invoke(this, EventArgs.Empty);
        return new AccountBindingResult(purged.Count, purged);
    }

    private JsonArray BuildDirtyConversations(long lastSyncMs, bool isFirstSync)
    {
        var dirty = new JsonArray();
        foreach (var row in _conversations.ListActive())
        {
            if (!IsCloudSyncable(row)) continue;

            var rows = _messages.List(row.Id);
            if (rows.Count == 0) continue;

            var uploadRow = EnsureGeneratedTitle(row, rows);
            if (!isFirstSync && uploadRow.UpdatedAt <= lastSyncMs) continue;

            // Use metadata-first sync: after the first sync,
            // manual/list sync only sends metadata for already-synced
            // conversations. Brand-new local conversations still include their
            // messages so sync.php can create the backing conversation file.
            var shouldIncludeMessages = isFirstSync || !HasConversationSyncTimestamp(row.Id);
            dirty.Add(new JsonObject
            {
                ["metadata"] = ToCloudMetadata(uploadRow),
                ["messages"] = shouldIncludeMessages ? ToCloudMessages(rows) : new JsonArray()
            });
        }

        return dirty;
    }

    private CloudMetadataMergeResult MergeServerMetadata(JsonObject syncResult, string syncTimestamp)
    {
        var upserted = 0;
        var remoteDeleted = 0;
        var changedRows = new List<ConversationRow>();
        var removedIds = new List<string>();

        if (syncResult["updated_content"] is JsonArray updatedContent)
        {
            foreach (var node in updatedContent.OfType<JsonObject>())
            {
                if (node["metadata"] is not JsonObject metadata) continue;
                var id = metadata["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var existing = _conversations.Get(id);
                var row = ToLocalConversation(metadata, existing);
                _conversations.Upsert(row);
                SaveCloudMetadata(id, metadata);
                SetConversationSyncTimestamp(id, syncTimestamp);
                if (node["messages"] is JsonArray messages && messages.Count > 0)
                    _messages.ReplaceConversationMessages(id, ToLocalMessages(id, messages));
                changedRows.Add(row);
                upserted++;
            }
        }

        if (syncResult["full_metadata_list"] is not JsonArray serverList)
            return new CloudMetadataMergeResult(upserted, remoteDeleted, changedRows, removedIds);

        var serverIds = new HashSet<string>(StringComparer.Ordinal);
        var merged = 0;
        foreach (var node in serverList)
        {
            if (node is not JsonObject metadata) continue;
            var id = metadata["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;
            serverIds.Add(id);

            var serverUpdatedAt = ParseCloudTimestamp(metadata["updated_at"] ?? metadata["time"]);
            var existing = _conversations.Get(id);
            if (existing is not null)
            {
                if (existing.DeletedAt is long deletedAt && deletedAt >= serverUpdatedAt)
                    continue;
                if (existing.DeletedAt is null && existing.UpdatedAt >= serverUpdatedAt)
                    continue;
            }

            var shouldRefreshDetail = existing is not null
                && existing.DeletedAt is null
                && serverUpdatedAt > existing.UpdatedAt
                && _messages.List(id).Count > 0;

            var row = ToLocalConversation(metadata, existing);
            _conversations.Upsert(row);
            SaveCloudMetadata(id, metadata);
            SetConversationSyncTimestamp(id, syncTimestamp);
            if (shouldRefreshDetail)
                _settings.Set(DetailRefreshKey(id), serverUpdatedAt.ToString(CultureInfo.InvariantCulture));
            changedRows.Add(row);
            merged++;
        }

        foreach (var row in _conversations.ListActive())
        {
            if (!IsCloudSyncable(row)) continue;
            if (!HasConversationSyncTimestamp(row.Id)) continue;
            if (serverIds.Contains(row.Id)) continue;

            _conversations.SoftDelete(row.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _settings.Remove(ConversationSyncKey(row.Id));
            _settings.Remove(CloudMetadataKey(row.Id));
            removedIds.Add(row.Id);
            remoteDeleted++;
        }

        return new CloudMetadataMergeResult(upserted + merged, remoteDeleted, changedRows, removedIds);
    }

    private static bool HasServerMetadata(JsonObject syncResult) =>
        syncResult["full_metadata_list"] is JsonArray { Count: > 0 };

    private static string DetailRefreshKey(string conversationId) => DetailRefreshPrefix + conversationId;

    private static string DetailParserVersionKey(string conversationId) => DetailParserVersionPrefix + conversationId;

    private static string TitleGeneratedKey(string conversationId) => TitleGeneratedPrefix + conversationId;

    private async Task<JsonObject?> FetchConversationAsync(string jwt, string conversationId, CancellationToken ct)
    {
        try
        {
            var result = await PostSyncAsync(jwt, new JsonObject
            {
                ["action"] = "fetch_conversation",
                ["conversation_id"] = conversationId
            }, ct).ConfigureAwait(false);

            return result["conversation"] as JsonObject;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteCloudConversationsAsync(string jwt, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var id in ids) array.Add(id);

        await PostSyncAsync(jwt, new JsonObject
        {
            ["action"] = "delete",
            ["conversation_ids"] = array
        }, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> PostSyncAsync(
        string jwt,
        JsonObject payload,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var endpoint = NetworkSecurity.RequireHttps(new Uri(Endpoint), "MolaGPT 云同步");
        var json = payload.ToJsonString(JsonOptions);

        HttpResponseMessage resp;
        if (Encoding.UTF8.GetByteCount(json) >= ChunkSize)
        {
            try
            {
                resp = await SendChunkedAsync(endpoint, jwt, payload, json, ct, progress).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report("分块上传失败，正在改用普通同步请求...");
                using var fallbackReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                fallbackReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                resp = await _http.SendAsync(fallbackReq, ct).ConfigureAwait(false);
            }
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"云同步请求失败：HTTP {(int)resp.StatusCode} {body}", null, resp.StatusCode);
            }

            var result = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("云同步返回为空。");
            if (result["success"]?.GetValue<bool>() == false)
            {
                var message = result["message"]?.GetValue<string>() ?? "云同步失败。";
                throw new InvalidOperationException(message);
            }

            return result;
        }
    }

    private async Task<HttpResponseMessage> SendChunkedAsync(
        Uri endpoint,
        string jwt,
        JsonObject payload,
        string json,
        CancellationToken ct,
        IProgress<string>? progress)
    {
        var chunks = SplitUtf8Chunks(json, ChunkSize).ToList();
        var totalChunks = chunks.Count;
        progress?.Report($"同步数据较大，正在初始化分块上传 0/{totalChunks}...");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                action = "initiate_chunked_sync",
                total_chunks = totalChunks,
                last_sync_timestamp = payload["last_sync_timestamp"]?.GetValue<string>() ?? EpochIso
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var initResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("分块上传初始化失败。");
        var uploadId = init["upload_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new InvalidOperationException(init["message"]?.GetValue<string>() ?? "分块上传初始化失败。");

        for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];

            progress?.Report($"正在上传同步数据 {chunkIndex + 1}/{totalChunks}...");
            using var chunkReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(chunk, Encoding.UTF8, "application/json")
            };
            chunkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            chunkReq.Headers.TryAddWithoutValidation("X-Upload-Id", uploadId);
            chunkReq.Headers.TryAddWithoutValidation("X-Chunk-Index", chunkIndex.ToString(CultureInfo.InvariantCulture));
            chunkReq.Headers.TryAddWithoutValidation("X-Total-Chunks", totalChunks.ToString(CultureInfo.InvariantCulture));

            using var chunkResp = await _http.SendAsync(chunkReq, ct).ConfigureAwait(false);
            chunkResp.EnsureSuccessStatusCode();
            var chunkResult = await chunkResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            if (chunkResult?["success"]?.GetValue<bool>() == false)
                throw new InvalidOperationException(chunkResult["message"]?.GetValue<string>() ?? $"分块 {chunkIndex + 1} 上传失败。");
        }

        progress?.Report("正在合并云端分块...");
        var finalReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { action = "complete_chunked_sync" })
        };
        finalReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        finalReq.Headers.TryAddWithoutValidation("X-Upload-Id", uploadId);
        return await _http.SendAsync(finalReq, ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> SplitUtf8Chunks(string text, int byteLimit)
    {
        var start = 0;
        while (start < text.Length)
        {
            var bytes = 0;
            var index = start;
            while (index < text.Length)
            {
                var charLen = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
                var charBytes = Encoding.UTF8.GetByteCount(text.AsSpan(index, charLen));
                if (bytes > 0 && bytes + charBytes > byteLimit) break;
                bytes += charBytes;
                index += charLen;
                if (bytes >= byteLimit) break;
            }

            if (index == start) index++;
            yield return text[start..index];
            start = index;
        }
    }

    private static string? ExtractGeneratedTitle(JsonObject result)
    {
        var title = result["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(title)
            && result["choices"] is JsonArray choices
            && choices.FirstOrDefault() is JsonObject choice
            && choice["message"] is JsonObject message)
        {
            title = message["content"]?.GetValue<string>();
        }

        if (string.IsNullOrWhiteSpace(title)) return null;

        title = title.Trim();
        title = Regex.Replace(title, "^[\"'“”‘’「」『』《》]+|[\"'“”‘’「」『』《》]+$", string.Empty);
        title = Regex.Replace(title, "^标题\\s*[:：]\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        title = title.Trim('。', '，', ',', '.', ':', '：', ';', '；', '!', '！', '?', '？');
        if (title.Length > 30) title = title[..30].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private JsonObject ToCloudMetadata(ConversationRow row)
    {
        var metadata = LoadCloudMetadata(row.Id) ?? new JsonObject();
        var created = ToIso(row.CreatedAt);
        var updated = ToIso(row.UpdatedAt);

        metadata["id"] = row.Id;
        metadata["title"] = string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title;
        metadata["time"] = created;
        metadata["updated_at"] = updated;
        if (!string.IsNullOrWhiteSpace(row.ModelId)) metadata["model"] = row.ModelId;
        else metadata.Remove("model");
        if (row.Pinned) metadata["pinned"] = true;
        else metadata.Remove("pinned");
        return metadata;
    }

    private ConversationRow EnsureGeneratedTitle(ConversationRow row, IReadOnlyList<MessageRow> rows)
    {
        if (!string.IsNullOrWhiteSpace(row.Title) && row.Title != "新对话")
            return row;

        var firstUser = rows.FirstOrDefault(message => message.Role == "user");
        var title = ChatViewModel.GenerateTitle(firstUser?.Content);
        if (title == row.Title) return row;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updated = row with
        {
            Title = title,
            UpdatedAt = Math.Max(row.UpdatedAt, now)
        };
        _conversations.Upsert(updated);
        return updated;
    }

    private static JsonArray ToCloudMessages(IReadOnlyList<MessageRow> rows)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var splitThinking = ChatViewModel.SplitInlineThinking(row.Content);
            var content = splitThinking.Visible;
            JsonArray? toolCalls = null;
            JsonArray? thinkingSegments = null;
            var message = new JsonObject
            {
                ["role"] = row.Role,
                ["content"] = content,
                ["timestamp"] = row.CreatedAt
            };

            string? thinkingText = splitThinking.Thinking;
            if (!string.IsNullOrWhiteSpace(row.Meta))
            {
                try
                {
                    using var doc = JsonDocument.Parse(row.Meta);
                    if (JsonNode.Parse(row.Meta) is JsonObject metaNode)
                    {
                        if (metaNode["content_parts"] is JsonNode contentParts)
                            message["content"] = contentParts.DeepClone();
                        if (metaNode["tool_calls"] is JsonArray toolCallsNode)
                            toolCalls = toolCallsNode.DeepClone() as JsonArray;
                        if (metaNode["thinking_segments"] is JsonArray thinkingSegmentsNode)
                            thinkingSegments = thinkingSegmentsNode.DeepClone() as JsonArray;
                        metaNode.Remove("thinking");
                        metaNode.Remove("provider");
                        metaNode.Remove("content_parts");
                        if (metaNode.Count > 0)
                            message["meta"] = metaNode.DeepClone();
                    }
                    if (doc.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                        message["model_label"] = model.GetString();
                    if (doc.RootElement.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                        thinkingText = MergeThinking(thinking.GetString(), thinkingText);
                }
                catch (JsonException) { }
            }

            var canUseTimelineContent = message["content"] is not JsonArray;
            if (canUseTimelineContent && HasTimelineItems(toolCalls, thinkingSegments))
            {
                message["content"] = BuildWebTimelineContent(content, toolCalls, thinkingSegments);
            }
            else if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                message["content"] = BuildWebCompatibleThinkingContent(content, thinkingText);
            }

            array.Add(message);
        }

        return array;
    }

    private static bool HasTimelineItems(JsonArray? toolCalls, JsonArray? thinkingSegments) =>
        (toolCalls is { Count: > 0 }) || (thinkingSegments is { Count: > 0 });

    private static string BuildWebTimelineContent(string visible, JsonArray? toolCalls, JsonArray? thinkingSegments)
    {
        var events = new List<CloudTimelineItem>();
        var order = 0;

        if (thinkingSegments is not null)
        {
            foreach (var node in thinkingSegments)
            {
                if (node is not JsonObject item) continue;
                var source = ReadString(item, "source");
                if (string.IsNullOrWhiteSpace(source)) continue;
                var hasExplicitToolCalls = toolCalls is { Count: > 0 };
                events.Add(new CloudTimelineItem(
                    ReadInt(item, "content_offset", "contentOffset") ?? 0,
                    ReadInt(item, "timeline_index", "timelineIndex") ?? order,
                    order++,
                    BuildWebThink(source!, emitEmbeddedTools: !hasExplicitToolCalls)));
            }
        }

        if (toolCalls is not null)
        {
            foreach (var node in toolCalls)
            {
                if (node is not JsonObject item) continue;
                var markup = BuildWebToolMarkup(item);
                if (string.IsNullOrWhiteSpace(markup)) continue;
                events.Add(new CloudTimelineItem(
                    ReadInt(item, "content_offset", "contentOffset") ?? 0,
                    ReadInt(item, "timeline_index", "timelineIndex") ?? order,
                    order++,
                    markup));
            }
        }

        if (events.Count == 0) return visible;

        var body = visible ?? string.Empty;
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var item in events
                     .OrderBy(e => Math.Clamp(e.ContentOffset, 0, body.Length))
                     .ThenBy(e => e.TimelineIndex)
                     .ThenBy(e => e.Order))
        {
            var offset = Math.Clamp(item.ContentOffset, 0, body.Length);
            if (offset > cursor)
            {
                builder.Append(body, cursor, offset - cursor);
                cursor = offset;
            }
            builder.Append(item.Markup);
        }

        if (cursor < body.Length)
            builder.Append(body, cursor, body.Length - cursor);

        return builder.ToString().Trim();
    }

    private static string BuildWebThink(string source, bool emitEmbeddedTools = true)
    {
        var thinking = source.Trim();
        if (string.IsNullOrWhiteSpace(thinking)) return string.Empty;
        var firstTool = FindNextWebToolMarker(thinking, 0);
        if (firstTool < 0)
            return "\n\n<think>\n" + thinking + "\n</think>\n\n";

        var builder = new StringBuilder();
        var pos = 0;

        void AppendThinkingText(string text)
        {
            var clean = text.Trim();
            if (string.IsNullOrWhiteSpace(clean)) return;
            builder.Append("\n\n<think>\n").Append(clean).Append("\n</think>\n\n");
        }

        while (pos < thinking.Length)
        {
            var next = FindNextWebToolMarker(thinking, pos);
            if (next < 0)
            {
                AppendThinkingText(thinking[pos..]);
                break;
            }

            if (next > pos)
                AppendThinkingText(thinking[pos..next]);

            var end = FindWebTimelineMarkerEnd(thinking, next);
            if (end < 0)
            {
                AppendThinkingText(thinking[next..]);
                break;
            }

            if (emitEmbeddedTools)
                builder.Append("\n\n").Append(thinking[next..end].Trim()).Append("\n\n");
            pos = end;
        }

        return builder.ToString();
    }

    private static string BuildWebCompatibleThinkingContent(string visible, string thinking)
    {
        var trimmedThinking = thinking.Trim();
        var trimmedVisible = visible.TrimStart();
        var thinkingMarkup = BuildWebThink(trimmedThinking).Trim();
        if (string.IsNullOrWhiteSpace(trimmedVisible))
            return thinkingMarkup;

        return thinkingMarkup + "\n\n" + trimmedVisible;
    }

    private static string BuildWebToolMarkup(JsonObject tool)
    {
        var name = ReadString(tool, "name") ?? "tool";
        var phase = MapWebToolPhase(ReadString(tool, "status"));
        var label = ReadString(tool, "label");
        if (string.IsNullOrWhiteSpace(label))
            label = ReadableToolLabel(name, phase);

        var toolType = WebToolType(name);
        if (toolType == "web_search")
            return BuildWebSearchMarkup(SearchChipsFromTool(tool, label!), phase);

        var extraClass = toolType == "image-gen" ? " tool-image-blockquote" : string.Empty;
        var dsBody = new StringBuilder();
        var args = ReadString(tool, "arguments_json", "argumentsJson");
        if (!string.IsNullOrWhiteSpace(args))
            dsBody.Append("**参数：**\n\n```json\n").Append(args).Append("\n```\n");
        var result = ReadString(tool, "result_preview_json", "resultPreviewJson");
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (dsBody.Length > 0) dsBody.Append('\n');
            dsBody.Append(result);
        }

        return "\n\n<blockquote class=\"tool-status " + phase + extraClass + "\"><p>" + EscapeHtml(label!) + "</p></blockquote>\n\n"
               + "<DSanalysis data-tool-type=\"" + toolType + "\" data-analysis-phase=\"" + phase + "\">"
               + dsBody
               + "</DSanalysis>\n\n";
    }

    private static string BuildWebSearchMarkup(IReadOnlyList<SearchChip> chips, string phase)
    {
        var safePhase = phase is "completed" or "error" or "analyzing" ? phase : "completed";
        var chipHtml = new StringBuilder();
        var renderChips = chips.Count == 0
            ? new[] { new SearchChip("联网搜索", Array.Empty<string>()) }
            : chips;
        foreach (var chip in renderChips)
        {
            chipHtml.Append("<span class=\"tool-search-chip\"><span class=\"tool-search-chip-icon\" aria-hidden=\"true\"><i class=\"fas fa-search\"></i></span>")
                .Append("<span class=\"tool-search-chip-text\">").Append(EscapeHtml(chip.Text)).Append("</span>");
            foreach (var badge in chip.Badges)
            {
                chipHtml.Append("<span class=\"tool-search-chip-badge\"><span class=\"tool-search-chip-badge-icon\"><i class=\"")
                    .Append(SearchBadgeIcon(badge))
                    .Append("\"></i></span>")
                    .Append(EscapeHtml(badge))
                    .Append("</span>");
            }
            chipHtml.Append("</span>");
        }

        return "\n\n<blockquote class=\"tool-status " + safePhase + " tool-search-blockquote\" data-search-phase=\"" + safePhase + "\">"
               + "<p class=\"tool-search-title\">网络搜索</p><div class=\"tool-search-chip-wrap\">" + chipHtml + "</div></blockquote>\n\n"
               + "<DSanalysis data-tool-type=\"web_search\" data-analysis-phase=\"" + safePhase + "\"></DSanalysis>\n\n";
    }

    private static IReadOnlyList<SearchChip> SearchChipsFromTool(JsonObject tool, string label)
    {
        var args = ReadString(tool, "arguments_json", "argumentsJson");
        var fromArgs = SearchChipsFromArgs(args);
        return fromArgs.Count > 0 ? fromArgs : SplitSearchChipText(label);
    }

    private static IReadOnlyList<SearchChip> SearchChipsFromArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (root.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array)
            {
                var chips = new List<SearchChip>();
                foreach (var item in queries.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) chips.Add(new SearchChip(text!, []));
                        continue;
                    }
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var query = ReadString(item, "query") ?? ReadString(item, "text") ?? ReadString(item, "q");
                    if (!string.IsNullOrWhiteSpace(query))
                        chips.Add(new SearchChip(query!, SearchBadgesFrom(item)));
                }
                return chips;
            }

            var single = ReadString(root, "query") ?? ReadString(root, "search_query") ?? ReadString(root, "q");
            return string.IsNullOrWhiteSpace(single) ? [] : [new SearchChip(single!, SearchBadgesFrom(root))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SearchChip> SplitSearchChipText(string rawText)
    {
        var normalized = Regex.Replace(rawText, "\\s+", " ");
        normalized = Regex.Replace(
            normalized,
            "^\\s*(?:✓\\s*)?(?:网络搜索|联网搜索|搜索完成|网络搜索完成|联网搜索完成)\\s*[:：-]?\\s*",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return [];

        var slashParts = Regex.Split(normalized, "\\s*/\\s*")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (slashParts.Count > 1)
            return slashParts.Select(ParseSearchChipWithTrailingBadge).ToList();

        var matches = Regex.Matches(normalized, "(.+?)\\s+(news|finance|paper|technology|day|week|month|year)(?=\\s+|$)", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            return matches
                .Select(m => new SearchChip(m.Groups[1].Value.Trim(), [m.Groups[2].Value.ToLowerInvariant()]))
                .Where(c => c.Text.Length > 0)
                .ToList();
        }

        return [ParseSearchChipWithTrailingBadge(normalized)];
    }

    private static SearchChip ParseSearchChipWithTrailingBadge(string value)
    {
        var match = Regex.Match(value.Trim(), "^(.+?)\\s+(news|finance|paper|technology|day|week|month|year)$", RegexOptions.IgnoreCase);
        return match.Success
            ? new SearchChip(match.Groups[1].Value.Trim(), [match.Groups[2].Value.ToLowerInvariant()])
            : new SearchChip(value.Trim(), []);
    }

    private static IReadOnlyList<string> SearchBadgesFrom(JsonElement obj)
    {
        var badges = new List<string>();
        foreach (var name in new[] { "topic", "time_range", "country" })
        {
            var value = ReadString(obj, name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            badges.Add(name == "country" ? value!.ToUpperInvariant() : value!);
        }
        return badges;
    }

    private static string SearchBadgeIcon(string badge) => badge.ToLowerInvariant() switch
    {
        "news" => "far fa-newspaper",
        "finance" => "fas fa-chart-line",
        "paper" => "fas fa-graduation-cap",
        "technology" => "fas fa-microchip",
        "day" or "week" or "month" or "year" => "far fa-clock",
        _ => badge.Length == 2 && badge.All(char.IsLetter) ? "fas fa-globe-asia" : "fas fa-tag"
    };

    private static string WebToolType(string name) => name switch
    {
        "image-gen" or "image_generation" or "image_generation_and_editing" or "draw_with_canvas" => "image-gen",
        "image-analyze" or "image_analyze" or "analyze_sandbox_image" => "image-analyze",
        "image-action" or "image_action" or "image_file_process" => "image-action",
        "search_web" or "web_search" => "web_search",
        "steel_browser" or "browser" or "web_fetch" => "tool-call",
        "execute_python_code" or "python" => "python",
        "mcp" => "mcp",
        _ => "tool-call"
    };

    private static string ReadableToolLabel(string name, string phase)
    {
        var completed = phase == "completed";
        var error = phase == "error";
        return WebToolType(name) switch
        {
            "image-gen" => completed ? "绘制完成" : error ? "绘制失败" : "正在绘制",
            "image-analyze" => completed ? "图片分析完成" : error ? "图片分析失败" : "正在查看图片",
            "image-action" => completed ? "图片处理完成" : error ? "图片处理失败" : "正在处理图片",
            "python" => completed ? "Python 执行完成" : error ? "Python 执行失败" : "正在执行 Python",
            "mcp" => completed ? "连接器调用完成" : error ? "连接器调用失败" : "正在调用连接器",
            "web_search" or "search_web" => completed ? "联网搜索完成" : error ? "联网搜索失败" : "正在访问互联网",
            _ when name is "web_fetch" or "steel_browser" => completed ? "阅读网页" : error ? "网页阅读失败" : "正在阅读网页",
            _ => completed ? "工具调用完成" : error ? "工具调用失败" : "正在处理..."
        };
    }

    private static string MapWebToolPhase(string? status) => status?.ToLowerInvariant() switch
    {
        "completed" or "complete" or "success" or "succeeded" => "completed",
        "error" or "failed" or "failure" => "error",
        _ => "analyzing"
    };

    private static string? ReadString(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }
        return null;
    }

    private static int? ReadInt(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value && value.TryGetValue<int>(out var number))
                return number;
        }
        return null;
    }

    private static double? ReadDouble(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value && value.TryGetValue<double>(out var number))
                return number;
        }
        return null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString();
    }

    private static string EscapeHtml(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private sealed record CloudTimelineItem(int ContentOffset, int TimelineIndex, int Order, string Markup);

    private sealed record SearchChip(string Text, IReadOnlyList<string> Badges);

    private static LocalTimelineParse ParseWebTimelineForLocal(string source)
    {
        var visible = new StringBuilder();
        var thinkingSegments = new JsonArray();
        var toolCalls = new JsonArray();
        var thinking = new StringBuilder();
        var pos = 0;
        var timeline = 0;
        var hasTimeline = false;
        var lastTimelineItemWasTool = false;

        void AppendVisible(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var clean = MessageViewModel.StripSystemHints(text);
            visible.Append(clean);
            if (!string.IsNullOrWhiteSpace(clean))
                lastTimelineItemWasTool = false;
        }

        void AppendThinking(string text)
        {
            var clean = text.Trim();
            if (string.IsNullOrWhiteSpace(clean)) return;
            var offset = visible.Length;
            thinkingSegments.Add(new JsonObject
            {
                ["source"] = clean,
                ["content_offset"] = offset,
                ["timeline_index"] = timeline++,
                ["elapsed_seconds"] = 0
            });
            if (thinking.Length > 0) thinking.Append("\n\n");
            thinking.Append(clean);
            hasTimeline = true;
            lastTimelineItemWasTool = false;
        }

        void AppendThinkingWithEmbeddedTools(string text)
        {
            var posInThinking = 0;
            while (posInThinking < text.Length)
            {
                var nextTool = FindNextWebToolMarker(text, posInThinking);
                if (nextTool < 0)
                {
                    AppendThinking(text[posInThinking..]);
                    break;
                }

                if (nextTool > posInThinking)
                    AppendThinking(text[posInThinking..nextTool]);

                var end = FindWebTimelineMarkerEnd(text, nextTool);
                if (end < 0)
                {
                    AppendThinking(text[nextTool..]);
                    break;
                }

                AppendTool(text[nextTool..end]);
                posInThinking = end;
            }
        }

        void AppendTool(string unit)
        {
            if (StartsWithIgnoreCase(unit, 0, "<DSanalysis")
                && lastTimelineItemWasTool
                && TryMergeDsAnalysisWithPreviousTool(unit, toolCalls))
            {
                hasTimeline = true;
                return;
            }

            var tool = ParseWebToolUnitForLocal(unit, visible.Length, timeline);
            if (tool is null) return;
            toolCalls.Add(tool);
            timeline++;
            hasTimeline = true;
            lastTimelineItemWasTool = true;
        }

        while (pos < source.Length)
        {
            var next = FindNextWebTimelineMarker(source, pos);
            if (next < 0)
            {
                AppendVisible(source[pos..]);
                break;
            }

            if (next > pos)
                AppendVisible(source[pos..next]);

            if (StartsWithIgnoreCase(source, next, "<think"))
            {
                var openEnd = source.IndexOf('>', next);
                if (openEnd < 0)
                {
                    AppendThinking(source[next..]);
                    break;
                }

                var close = IndexOfIgnoreCase(source, "</think>", openEnd + 1);
                var body = close < 0
                    ? source[(openEnd + 1)..]
                    : source.Substring(openEnd + 1, close - openEnd - 1);
                AppendThinkingWithEmbeddedTools(body);
                pos = close < 0 ? source.Length : close + "</think>".Length;
                continue;
            }

            var end = FindWebTimelineMarkerEnd(source, next);
            if (end < 0)
            {
                AppendVisible(source.Substring(next, 1));
                pos = next + 1;
                continue;
            }

            var unit = source[next..end];
            if (StartsWithToolStatusBlockquote(source, next))
            {
                var afterStatus = SkipWhitespace(source, end);
                if (StartsWithIgnoreCase(source, afterStatus, "<DSanalysis"))
                {
                    var dsEnd = FindWebTimelineMarkerEnd(source, afterStatus);
                    if (dsEnd > afterStatus)
                    {
                        var dsUnit = source[afterStatus..dsEnd];
                        if (ShouldKeepDsAnalysisInVisibleContent(dsUnit))
                        {
                            AppendVisible(source[next..dsEnd]);
                            pos = dsEnd;
                            continue;
                        }
                    }
                }
            }

            if (ShouldKeepDsAnalysisInVisibleContent(unit))
                AppendVisible(unit);
            else
                AppendTool(unit);
            pos = end;
        }

        return new LocalTimelineParse(
            visible.ToString(),
            thinking.Length == 0 ? null : thinking.ToString(),
            toolCalls,
            thinkingSegments,
            hasTimeline);
    }

    private static int SkipWhitespace(string source, int start)
    {
        var pos = Math.Clamp(start, 0, source.Length);
        while (pos < source.Length && char.IsWhiteSpace(source[pos]))
            pos++;
        return pos;
    }

    private static bool ShouldKeepDsAnalysisInVisibleContent(string unit)
    {
        return DsDataToolType(unit) is "python" or "mcp" or "image-action";
    }

    private static string? DsDataToolType(string unit)
    {
        if (!StartsWithIgnoreCase(unit, 0, "<DSanalysis")) return null;
        var openEnd = unit.IndexOf('>');
        if (openEnd < 0) return null;

        var match = Regex.Match(
            unit[..openEnd],
            "\\bdata-tool-type\\s*=\\s*([\"'])(?<type>[^\"']+)\\1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["type"].Value.Trim().ToLowerInvariant() : null;
    }

    private static JsonObject? ParseWebToolUnitForLocal(string unit, int contentOffset, int timelineIndex)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        var isDs = StartsWithIgnoreCase(unit, 0, "<DSanalysis");
        var body = isDs ? StripTagPair(unit, "DSanalysis").Trim() : string.Empty;
        if (isDs && string.IsNullOrWhiteSpace(body)) return null;

        var name = isDs ? DsToolName(unit) : ToolNameFromStatusMarkup(unit);
        var phase = ExtractToolPhase(unit);
        var label = isDs
            ? ReadableToolLabel(name, phase)
            : ExtractSteelStepTitle(unit) ?? HtmlToText(unit);
        if (string.IsNullOrWhiteSpace(label))
            label = ReadableToolLabel(name, phase);

        var obj = new JsonObject
        {
            ["id"] = "cloud-tool-" + timelineIndex,
            ["name"] = name,
            ["status"] = phase == "error" ? "error" : phase == "completed" ? "completed" : "running",
            ["label"] = label,
            ["content_offset"] = contentOffset,
            ["timeline_index"] = timelineIndex,
            ["provider"] = "MolaGPT"
        };
        if (name == "search_web")
        {
            var chips = ExtractSearchChips(unit);
            obj["label"] = "网络搜索";
            obj["summary"] = string.Join(" / ", chips.Select(c => c.Text)).Trim();
            obj["arguments_json"] = BuildSearchArgumentsJson(chips);
        }
        else if (name == "web_fetch" && !string.IsNullOrWhiteSpace(body))
            ApplyWebFetchResult(obj, body);
        else if (!string.IsNullOrWhiteSpace(body))
            obj["detail"] = body;
        else if (StartsWithIgnoreCase(unit, 0, "<steel-step"))
        {
            var preview = ExtractSteelStepPreview(unit);
            if (!string.IsNullOrWhiteSpace(preview))
                obj["detail"] = preview;
            if (name is "web_fetch" or "steel_browser")
                obj["provider"] = "Steel Browser";
        }
        return obj;
    }

    private static bool TryMergeDsAnalysisWithPreviousTool(string unit, JsonArray toolCalls)
    {
        var body = StripTagPair(unit, "DSanalysis").Trim();
        if (string.IsNullOrWhiteSpace(body)) return true;
        if (toolCalls.Count == 0 || toolCalls[toolCalls.Count - 1] is not JsonObject previous) return false;

        var name = DsToolName(unit);
        var phase = ExtractToolPhase(unit);
        previous["name"] = name;
        previous["status"] = phase == "error" ? "error" : phase == "completed" ? "completed" : "running";
        if (name == "web_fetch")
        {
            if (string.IsNullOrWhiteSpace(ReadString(previous, "label")))
                previous["label"] = ReadableToolLabel(name, phase);
            ApplyWebFetchResult(previous, body);
        }
        else
        {
            previous["label"] = ReadableToolLabel(name, phase);
            previous["detail"] = body;
        }
        return true;
    }

    private static void ApplyWebFetchResult(JsonObject tool, string body)
    {
        tool["provider"] = "Steel Browser";
        tool["result_preview_json"] = body;

        var existingDetail = ReadString(tool, "detail");
        if (!string.IsNullOrWhiteSpace(existingDetail)) return;

        var summary = ExtractDsField(body, "URL")
            ?? ExtractDsField(body, "页面标题")
            ?? ExtractMarkdownLinkTarget(body);
        if (!string.IsNullOrWhiteSpace(summary))
            tool["detail"] = TruncateSingleLine(summary!, 180);
    }

    private static string? ExtractDsField(string body, string fieldName)
    {
        var match = Regex.Match(
            body,
            @"(?im)^\s*-\s*(?:\*\*)?" + Regex.Escape(fieldName) + @"(?:\*\*)?\s*[:：]\s*(.+?)\s*$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractMarkdownLinkTarget(string body)
    {
        var match = Regex.Match(body, @"\[[^\]]+\]\((https?://[^)\s]+)\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string ToolNameFromStatusMarkup(string unit)
    {
        if (unit.Contains("tool-search-blockquote", StringComparison.OrdinalIgnoreCase)) return "search_web";
        if (unit.Contains("tool-steel-step", StringComparison.OrdinalIgnoreCase)) return SteelStepToolName(unit);
        var text = HtmlToText(unit);
        if (text.Contains("搜索", StringComparison.OrdinalIgnoreCase)) return "search_web";
        if (text.Contains("查看图片", StringComparison.OrdinalIgnoreCase) || text.Contains("图片分析", StringComparison.OrdinalIgnoreCase)) return "image-analyze";
        if (text.Contains("绘制", StringComparison.OrdinalIgnoreCase)) return "image-gen";
        if (text.Contains("Python", StringComparison.OrdinalIgnoreCase)) return "python";
        if (text.Contains("阅读网页", StringComparison.OrdinalIgnoreCase)
            || text.Contains("读取网页", StringComparison.OrdinalIgnoreCase)
            || text.Contains("查看网页", StringComparison.OrdinalIgnoreCase)) return "web_fetch";
        return "tool";
    }

    private static string DsToolName(string unit)
    {
        var lower = unit.ToLowerInvariant();
        if (lower.Contains("web_search") || lower.Contains("search_web")) return "search_web";
        if (lower.Contains("image-analyze")) return "image-analyze";
        if (lower.Contains("image-gen")) return "image-gen";
        if (lower.Contains("image-action")) return "image-action";
        if (lower.Contains("操作类型: screenshot_analyze")
            || lower.Contains("**操作类型:** screenshot_analyze")
            || lower.Contains("operation type: screenshot_analyze")
            || lower.Contains("操作类型: scrape")
            || lower.Contains("**操作类型:** scrape")
            || lower.Contains("operation type: scrape")
            || lower.Contains("steel browser截图")
            || (lower.Contains("页面标题") && lower.Contains("内容长度")))
            return "web_fetch";
        if (lower.Contains("python")) return "python";
        if (lower.Contains("mcp")) return "mcp";
        return "tool";
    }

    private static string SteelStepToolName(string unit)
    {
        var title = ExtractSteelStepTitle(unit) ?? string.Empty;
        if (unit.Contains("data-steel-action=\"scrape\"", StringComparison.OrdinalIgnoreCase)
            || unit.Contains("data-steel-action='scrape'", StringComparison.OrdinalIgnoreCase)
            || unit.Contains("data-steel-action=\"screenshot_analyze\"", StringComparison.OrdinalIgnoreCase)
            || unit.Contains("data-steel-action='screenshot_analyze'", StringComparison.OrdinalIgnoreCase)
            || title.Contains("阅读网页", StringComparison.OrdinalIgnoreCase)
            || title.Contains("读取网页", StringComparison.OrdinalIgnoreCase)
            || title.Contains("查看网页", StringComparison.OrdinalIgnoreCase))
            return "web_fetch";
        return "steel_browser";
    }

    private static string? ExtractSteelStepTitle(string html)
    {
        var match = Regex.Match(
            html,
            "<p\\b[^>]*class=[\"'][^\"']*\\btool-steel-step-title\\b[^\"']*[\"'][^>]*>([\\s\\S]*?)</p>",
            RegexOptions.IgnoreCase);
        return match.Success ? HtmlToText(match.Groups[1].Value) : null;
    }

    private static string? ExtractSteelStepPreview(string html)
    {
        var matches = Regex.Matches(
            html,
            "<span\\b[^>]*class=[\"'][^\"']*\\btool-steel-meta-item\\b[^\"']*[\"'][^>]*>([\\s\\S]*?)</span>",
            RegexOptions.IgnoreCase);
        var items = matches
            .Select(match => HtmlToText(match.Groups[1].Value))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .ToArray();
        return items.Length == 0 ? null : string.Join("\n", items);
    }

    private static string ExtractToolPhase(string unit)
    {
        var lower = unit.ToLowerInvariant();
        if (lower.Contains("data-analysis-phase=\"error\"") || lower.Contains(" tool-status error") || lower.Contains(" error\"")) return "error";
        return "completed";
    }

    private static IReadOnlyList<SearchChip> ExtractSearchChips(string html)
    {
        var chipMatches = Regex.Matches(
            html,
            "<span\\b[^>]*class=[\"'][^\"']*(?<![\\w-])tool-search-chip(?![\\w-])[^\"']*[\"'][^>]*>([\\s\\S]*?)(?=<span\\b[^>]*class=[\"'][^\"']*(?<![\\w-])tool-search-chip(?![\\w-])|</div>|</blockquote>|$)",
            RegexOptions.IgnoreCase);
        var chips = new List<SearchChip>();
        foreach (Match chipMatch in chipMatches)
        {
            var chipHtml = chipMatch.Groups[1].Value;
            var textMatch = Regex.Match(
                chipHtml,
                "<span\\b[^>]*class=[\"'][^\"']*\\btool-search-chip-text\\b[^\"']*[\"'][^>]*>([\\s\\S]*?)</span>",
                RegexOptions.IgnoreCase);
            if (!textMatch.Success) continue;
            var text = HtmlToText(textMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var badges = Regex.Matches(
                    chipHtml,
                    "<span\\b[^>]*class=[\"'][^\"']*\\btool-search-chip-badge\\b[^\"']*[\"'][^>]*>\\s*(?:<span\\b[\\s\\S]*?</span>)?\\s*([^<]+)\\s*</span>",
                    RegexOptions.IgnoreCase)
                .Select(match => HtmlToText(match.Groups[1].Value))
                .Where(badge => !string.IsNullOrWhiteSpace(badge))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            chips.Add(new SearchChip(text, badges));
        }

        if (chips.Count > 0) return chips;

        var textMatches = Regex.Matches(
            html,
            "<span\\b[^>]*class=[\"'][^\"']*\\btool-search-chip-text\\b[^\"']*[\"'][^>]*>([\\s\\S]*?)</span>",
            RegexOptions.IgnoreCase);
        return textMatches
            .Select(match => HtmlToText(match.Groups[1].Value))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => new SearchChip(text, []))
            .ToArray();
    }

    private static string BuildSearchArgumentsJson(IReadOnlyList<SearchChip> chips)
    {
        var queries = new JsonArray();
        foreach (var chip in chips)
        {
            var query = new JsonObject { ["query"] = chip.Text };
            foreach (var badge in chip.Badges)
            {
                var normalized = badge.Trim();
                if (normalized.Length == 0) continue;
                var lower = normalized.ToLowerInvariant();
                if (lower is "day" or "week" or "month" or "year")
                    query["time_range"] = lower;
                else if (normalized.Length == 2 && normalized.All(char.IsLetter))
                    query["country"] = normalized.ToUpperInvariant();
                else if (!query.ContainsKey("topic"))
                    query["topic"] = lower;
            }
            queries.Add(query);
        }

        return new JsonObject { ["queries"] = queries }.ToJsonString(JsonOptions);
    }

    private static string StripTagPair(string source, string tag)
    {
        var openEnd = source.IndexOf('>');
        if (openEnd < 0) return source;
        var close = IndexOfIgnoreCase(source, "</" + tag + ">", openEnd + 1);
        return close < 0 ? source[(openEnd + 1)..] : source.Substring(openEnd + 1, close - openEnd - 1);
    }

    private static string HtmlToText(string html) =>
        Regex.Replace(html, "<[^>]+>", string.Empty)
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Trim();

    private static int FindNextWebTimelineMarker(string source, int start) =>
        MinPositive(
            IndexOfIgnoreCase(source, "<think", start),
            FindNextWebToolMarker(source, start));

    private static int FindNextWebToolMarker(string source, int start) =>
        MinPositive(
            IndexOfIgnoreCase(source, "<steel-step", start),
            IndexOfIgnoreCase(source, "<DSanalysis", start),
            IndexOfNextToolStatusBlockquote(source, start));

    private static int FindWebTimelineMarkerEnd(string source, int start)
    {
        if (StartsWithIgnoreCase(source, start, "<steel-step")) return FindTagEnd(source, start, "</steel-step>");
        if (StartsWithIgnoreCase(source, start, "<DSanalysis")) return FindTagEnd(source, start, "</DSanalysis>");
        if (StartsWithToolStatusBlockquote(source, start)) return FindTagEnd(source, start, "</blockquote>");
        return -1;
    }

    private static int FindTagEnd(string source, int start, string closeTag)
    {
        var close = IndexOfIgnoreCase(source, closeTag, start);
        return close < 0 ? source.Length : close + closeTag.Length;
    }

    private static int IndexOfNextToolStatusBlockquote(string source, int start)
    {
        var pos = IndexOfIgnoreCase(source, "<blockquote", start);
        while (pos >= 0)
        {
            if (StartsWithToolStatusBlockquote(source, pos)) return pos;
            pos = IndexOfIgnoreCase(source, "<blockquote", pos + "<blockquote".Length);
        }
        return -1;
    }

    private static bool StartsWithToolStatusBlockquote(string source, int start)
    {
        if (!StartsWithIgnoreCase(source, start, "<blockquote")) return false;
        var openEnd = source.IndexOf('>', start);
        if (openEnd < 0) return false;
        return source.Substring(start, openEnd - start + 1).Contains("tool-status", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithIgnoreCase(string source, int start, string value) =>
        start >= 0
        && start + value.Length <= source.Length
        && source.AsSpan(start, value.Length).Equals(value.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static int IndexOfIgnoreCase(string source, string value, int start = 0) =>
        source.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);

    private static int MinPositive(params int[] values)
    {
        var min = -1;
        foreach (var value in values)
        {
            if (value < 0) continue;
            if (min < 0 || value < min) min = value;
        }
        return min;
    }

    private sealed record LocalTimelineParse(
        string Visible,
        string? Thinking,
        JsonArray ToolCalls,
        JsonArray ThinkingSegments,
        bool HasTimeline);

    private static ConversationRow ToLocalConversation(JsonObject metadata, ConversationRow? existing)
    {
        var id = metadata["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var created = ParseCloudTimestamp(metadata["time"]) is var time && time > 0
            ? time
            : existing?.CreatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updated = ParseCloudTimestamp(metadata["updated_at"] ?? metadata["time"]);
        if (updated <= 0) updated = created;

        return new ConversationRow(
            Id: id,
            Title: metadata["title"]?.GetValue<string>() ?? existing?.Title ?? "新对话",
            ModelId: metadata["model"]?.GetValue<string>() ?? existing?.ModelId,
            ProviderId: existing?.ProviderId ?? MolaGptProviderId,
            CreatedAt: created,
            UpdatedAt: updated,
            Pinned: metadata["pinned"]?.GetValue<bool>() ?? existing?.Pinned ?? false,
            DeletedAt: null);
    }

    private static bool IsCloudSyncable(ConversationRow row) =>
        string.Equals(row.ProviderId, MolaGptProviderId, StringComparison.OrdinalIgnoreCase);

    private bool HasConversationSyncTimestamp(string conversationId) =>
        !string.IsNullOrWhiteSpace(_settings.Get(ConversationSyncKey(conversationId)));

    private void SetConversationSyncTimestamp(string conversationId, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(timestamp)) return;
        _settings.Set(ConversationSyncKey(conversationId), timestamp);
    }

    private IReadOnlyList<ConversationRow> MarkUploadedConversations(JsonArray dirty, string syncTimestamp)
    {
        var rows = new List<ConversationRow>();
        foreach (var item in dirty.OfType<JsonObject>())
        {
            if (item["metadata"] is not JsonObject metadata) continue;
            var id = metadata["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;
            SetConversationSyncTimestamp(id, syncTimestamp);
            SaveCloudMetadata(id, metadata);
            if (_conversations.Get(id) is { } row)
                rows.Add(row);
        }

        return rows;
    }

    private JsonObject? LoadCloudMetadata(string conversationId)
    {
        var raw = _settings.Get(CloudMetadataKey(conversationId));
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private void SaveCloudMetadata(string conversationId, JsonObject metadata)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        _settings.Set(CloudMetadataKey(conversationId), metadata.ToJsonString(JsonOptions));
    }

    private static string ConversationSyncKey(string conversationId) => ConversationSyncPrefix + conversationId;

    private static string CloudMetadataKey(string conversationId) => ConversationMetadataPrefix + conversationId;

    private static IEnumerable<MessageRow> ToLocalMessages(string conversationId, JsonArray messages)
    {
        var lastCreatedAt = long.MinValue;
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) continue;
            var role = msg["role"]?.GetValue<string>() ?? "assistant";
            var content = ExtractContentText(msg["content"]);
            var timeline = role == "assistant"
                ? ParseWebTimelineForLocal(content)
                : new LocalTimelineParse(content, null, new JsonArray(), new JsonArray(), false);
            var splitThinking = timeline.HasTimeline
                ? new InlineThinkingParts(timeline.Visible, timeline.Thinking)
                : ChatViewModel.SplitInlineThinking(content);
            content = splitThinking.Visible;
            if (string.IsNullOrWhiteSpace(content) && role != "assistant") continue;

            var createdAt = ParseMessageTimestamp(msg["timestamp"]);
            if (createdAt <= lastCreatedAt)
                createdAt = lastCreatedAt + 1;
            lastCreatedAt = createdAt;
            var meta = new JsonObject();
            var messageMeta = msg["meta"] as JsonObject;
            if (msg["model_label"] is JsonNode modelLabel) meta["model"] = modelLabel.DeepClone();
            if (msg["model"] is JsonNode model) meta["model"] = model.DeepClone();
            if (messageMeta is not null)
            {
                foreach (var kv in messageMeta)
                    if (kv.Value is JsonNode metaNode)
                        meta[kv.Key] = metaNode.DeepClone();
            }
            if (msg["content"] is JsonArray rawContent)
                meta["content_parts"] = rawContent.DeepClone();
            if (!meta.ContainsKey("sources") && msg["sources"] is JsonNode directSources)
                meta["sources"] = directSources.DeepClone();
            if (timeline.ToolCalls.Count > 0)
                meta["tool_calls"] = timeline.ToolCalls.DeepClone();
            if (timeline.ThinkingSegments.Count > 0)
                meta["thinking_segments"] = CloneLocalThinkingSegmentsWithExistingTiming(timeline.ThinkingSegments, messageMeta);
            string? thinkingText = null;
            if (msg["reasoning_content"] is JsonNode thinking)
            {
                try { thinkingText = thinking.GetValue<string>(); }
                catch (InvalidOperationException) { }
            }
            if (!string.IsNullOrWhiteSpace(splitThinking.Thinking))
                thinkingText = MergeThinking(thinkingText, splitThinking.Thinking);
            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                meta["thinking"] = thinkingText.Trim();
            }

            yield return new MessageRow(
                Id: Guid.NewGuid().ToString("N"),
                ConversationId: conversationId,
                Role: role,
                Content: content,
                Meta: meta.Count == 0 ? null : meta.ToJsonString(JsonOptions),
                CreatedAt: createdAt);
        }
    }

    private static JsonArray CloneLocalThinkingSegmentsWithExistingTiming(JsonArray parsedSegments, JsonObject? messageMeta)
    {
        var clone = parsedSegments.DeepClone() as JsonArray ?? new JsonArray();
        if (messageMeta?["thinking_segments"] is not JsonArray existingSegments)
            return clone;

        var elapsedBySource = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var node in existingSegments)
        {
            if (node is not JsonObject item) continue;
            var source = ReadString(item, "source");
            var elapsed = ReadDouble(item, "elapsed_seconds", "elapsedSeconds");
            if (!string.IsNullOrWhiteSpace(source) && elapsed is > 0)
                elapsedBySource[source] = elapsed.Value;
        }

        foreach (var node in clone)
        {
            if (node is not JsonObject item) continue;
            var source = ReadString(item, "source");
            if (!string.IsNullOrWhiteSpace(source) && elapsedBySource.TryGetValue(source, out var elapsed))
                item["elapsed_seconds"] = elapsed;
        }

        return clone;
    }

    private static string? MergeThinking(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(second)) return first;
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (first.Contains(second, StringComparison.Ordinal)) return first;
        if (second.Contains(first, StringComparison.Ordinal)) return second;
        return first.TrimEnd() + "\n\n" + second.TrimStart();
    }

    private static string ExtractContentText(JsonNode? content)
    {
        if (content is null) return string.Empty;
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray array) return content.ToJsonString(JsonOptions);

        var parts = new List<string>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var type = item["type"]?.GetValue<string>();
            if (type == "text" && item["text"]?.GetValue<string>() is { } textPart)
                parts.Add(textPart);
            else if (type == "image_url")
                parts.Add("[图片]");
        }

        return string.Join("\n", parts);
    }

    private static long ParseMessageTimestamp(JsonNode? node)
    {
        if (node is null) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
                return longValue > 9_999_999_999 ? longValue : longValue * 1000;
            if (value.TryGetValue<double>(out var doubleValue))
            {
                var number = (long)doubleValue;
                return number > 9_999_999_999 ? number : number * 1000;
            }
            if (value.TryGetValue<string>(out var text))
                return ToUnixMilliseconds(text);
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static long ParseCloudTimestamp(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return ToUnixMilliseconds(text);
        return 0;
    }

    private static long ToUnixMilliseconds(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0;
    }

    private static string ToIso(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private void PublishStatus(CloudSyncState state, string message)
    {
        StatusChanged?.Invoke(this, new CloudSyncStatusChangedEventArgs(state, message, DateTimeOffset.Now));
    }
}

public sealed record CloudSyncResult(
    int Uploaded,
    int Downloaded,
    int Deleted,
    string LastSyncTimestamp,
    IReadOnlyList<ConversationRow> ChangedRows,
    IReadOnlyList<string> RemovedIds,
    bool RequiresFullReload);

public sealed record CloudMetadataMergeResult(
    int Upserted,
    int RemoteDeleted,
    IReadOnlyList<ConversationRow> ChangedRows,
    IReadOnlyList<string> RemovedIds);

internal readonly record struct AccountBindingResult(
    int PurgedConversationCount,
    IReadOnlyList<string> PurgedIds)
{
    public static AccountBindingResult Empty { get; } = new(0, Array.Empty<string>());
}

public enum CloudSyncState
{
    Idle,
    Syncing,
    Success,
    Error,
    Disabled
}

public sealed record CloudSyncStatusChangedEventArgs(
    CloudSyncState State,
    string Message,
    DateTimeOffset Timestamp);
