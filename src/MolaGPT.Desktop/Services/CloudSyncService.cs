using System.Globalization;
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
    private const string DetailRefreshPrefix = "cloud_sync.detail_refresh.";
    private const string ConversationSyncPrefix = "cloud_sync.conversation_timestamp.";
    private const string ConversationMetadataPrefix = "cloud_sync.metadata.";
    private const string TitleGeneratedPrefix = "cloud_sync.ai_title_generated.";
    private const string MolaGptProviderId = "molagpt-proxy";
    private const string EpochIso = "1970-01-01T00:00:00.000Z";
    private const int ChunkSize = 5 * 1024 * 1024;
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(15);
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
                MarkUploadedConversations(dirty, syncTimestamp);
                return result;
            }, token).ConfigureAwait(false);

            if (publishStatus)
                PublishStatus(CloudSyncState.Success, "同步成功");
            return new CloudSyncResult(
                Uploaded: dirty.Count,
                Downloaded: merge.Upserted,
                Deleted: deletedIds.Length + merge.RemoteDeleted,
                LastSyncTimestamp: syncTimestamp);
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

        var refreshKey = DetailRefreshKey(conversationId);
        var needsRefresh = !string.IsNullOrWhiteSpace(_settings.Get(refreshKey));
        var existingMessages = await Task.Run(() => _messages.List(conversationId), ct)
            .ConfigureAwait(false);
        if (existingMessages.Count > 0 && !needsRefresh) return false;

        JsonObject? detail;
        try
        {
            PublishStatus(CloudSyncState.Syncing, "正在拉取对话...");
            detail = await FetchConversationAsync(jwt, conversationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        PublishStatus(CloudSyncState.Success, "对话已更新");
        return true;
    }

    public async Task<string?> CompleteConversationTurnAsync(string conversationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return null;

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
        while (Interlocked.Exchange(ref _isSyncing, 1) == 1)
            await Task.Delay(1000, ct).ConfigureAwait(false);

        try
        {
            var jwt = _auth.CurrentJwt;
            if (string.IsNullOrWhiteSpace(jwt)) return;
            if (bool.TryParse(_settings.Get(SyncEnabledKey), out var enabled) && !enabled) return;

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

        if (syncResult["updated_content"] is JsonArray updatedContent)
        {
            foreach (var node in updatedContent.OfType<JsonObject>())
            {
                if (node["metadata"] is not JsonObject metadata) continue;
                var id = metadata["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var existing = _conversations.Get(id);
                _conversations.Upsert(ToLocalConversation(metadata, existing));
                SaveCloudMetadata(id, metadata);
                SetConversationSyncTimestamp(id, syncTimestamp);
                if (node["messages"] is JsonArray messages && messages.Count > 0)
                    _messages.ReplaceConversationMessages(id, ToLocalMessages(id, messages));
                upserted++;
            }
        }

        if (syncResult["full_metadata_list"] is not JsonArray serverList)
            return new CloudMetadataMergeResult(upserted, remoteDeleted);

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
            remoteDeleted++;
        }

        return new CloudMetadataMergeResult(upserted + merged, remoteDeleted);
    }

    private static bool HasServerMetadata(JsonObject syncResult) =>
        syncResult["full_metadata_list"] is JsonArray { Count: > 0 };

    private static string DetailRefreshKey(string conversationId) => DetailRefreshPrefix + conversationId;

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
            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                var folded = ChatViewModel.FoldLeadingThinkingToolMarkup(content, thinkingText);
                content = folded.Visible;
                message["content"] = content;
                if (!string.IsNullOrWhiteSpace(folded.Thinking))
                    message["reasoning_content"] = folded.Thinking;
            }

            array.Add(message);
        }

        return array;
    }

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

    private void MarkUploadedConversations(JsonArray dirty, string syncTimestamp)
    {
        foreach (var item in dirty.OfType<JsonObject>())
        {
            if (item["metadata"] is not JsonObject metadata) continue;
            var id = metadata["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;
            SetConversationSyncTimestamp(id, syncTimestamp);
            SaveCloudMetadata(id, metadata);
        }
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
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) continue;
            var role = msg["role"]?.GetValue<string>() ?? "assistant";
            var content = ExtractContentText(msg["content"]);
            var splitThinking = ChatViewModel.SplitInlineThinking(content);
            content = splitThinking.Visible;
            if (string.IsNullOrWhiteSpace(content) && role != "assistant") continue;

            var createdAt = ParseMessageTimestamp(msg["timestamp"]);
            var meta = new JsonObject();
            if (msg["model_label"] is JsonNode modelLabel) meta["model"] = modelLabel.DeepClone();
            if (msg["model"] is JsonNode model) meta["model"] = model.DeepClone();
            if (msg["meta"] is JsonObject messageMeta)
            {
                foreach (var kv in messageMeta)
                    if (kv.Value is JsonNode metaNode)
                        meta[kv.Key] = metaNode.DeepClone();
            }
            if (msg["content"] is JsonArray rawContent)
                meta["content_parts"] = rawContent.DeepClone();
            if (!meta.ContainsKey("sources") && msg["sources"] is JsonNode directSources)
                meta["sources"] = directSources.DeepClone();
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
                var folded = ChatViewModel.FoldLeadingThinkingToolMarkup(content, thinkingText);
                content = folded.Visible;
                if (!string.IsNullOrWhiteSpace(folded.Thinking))
                    meta["thinking"] = folded.Thinking;
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

public sealed record CloudSyncResult(int Uploaded, int Downloaded, int Deleted, string LastSyncTimestamp);

public sealed record CloudMetadataMergeResult(int Upserted, int RemoteDeleted);

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
