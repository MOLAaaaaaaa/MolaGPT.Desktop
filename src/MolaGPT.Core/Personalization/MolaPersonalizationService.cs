using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Personalization;

/// <summary>
/// Write-operation result. The server's content guardrails reject writes with
/// 422 + a specific Chinese reason (sensitive attribute / unsafe content /
/// empty text), and 403 while the master toggle is off. That message is what
/// the user needs, so writes carry it instead of a plain bool.
/// </summary>
public sealed record PersonalizationResult(bool Succeeded, string? Message = null)
{
    public static PersonalizationResult Ok { get; } = new(true);
    public static PersonalizationResult Fail(string? message) => new(false, message);
}

/// <summary>
/// Personalization data API (<c>user_data_manager.php</c>): long-term memory
/// entries, pending candidates, and style preferences. Ports the mobile
/// <c>UserDataApi</c> contract 1:1 — same endpoints, same actions, same
/// tolerant parsing (PHP PDO returns numeric columns as strings).
///
/// Auth uses the persistent login JWT, same as sync. Reads return null on
/// transport failure (empty list = genuinely empty); writes return a
/// <see cref="PersonalizationResult"/> carrying the server's Chinese reason.
/// </summary>
public sealed class MolaPersonalizationService
{
    public const string RelativePath = "api/auth/user_data_manager.php";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly MolaGptAuthService _auth;
    private readonly string _baseUrl;

    public MolaPersonalizationService(HttpClient http, MolaGptAuthService auth, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _baseUrl = baseUrl ?? "https://chatgpt.wljay.cn/v2/";
    }

    public async Task<MemoryEntriesResult?> GetMemoryEntriesAsync(CancellationToken ct = default)
    {
        var resp = await PostAsync(new JsonObject { ["action"] = "get_memory_entries" }, ct).ConfigureAwait(false);
        if (resp is null || !IsSuccess(resp)) return null;
        var entries = (resp["entries"] as JsonArray)?
            .Select((el, i) => el is JsonObject o ? ParseEntry(o, i) : null)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList() ?? [];
        var projection = resp["projection"] is JsonObject p ? ParseProjection(p) : new MemoryProjection();
        return new MemoryEntriesResult(entries, projection, GetBool(resp["memory_enabled"], true));
    }

    /// <param name="candidateId">Non-empty means "confirm this candidate" — the
    /// text was already normalized and guardrailed server-side.</param>
    public Task<PersonalizationResult> AddMemoryEntryAsync(
        string text, MemorySection section, string? candidateId = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["action"] = "add_memory_entry",
            ["text"] = text,
            ["section"] = MemorySections.Wire(section)
        };
        if (!string.IsNullOrEmpty(candidateId)) body["candidate_id"] = candidateId;
        return WriteAsync(body, ct);
    }

    public Task<PersonalizationResult> UpdateMemoryEntryAsync(
        string entryId, string newText, CancellationToken ct = default) =>
        WriteAsync(new JsonObject
        {
            ["action"] = "update_memory_entry",
            ["entry_id"] = entryId,
            ["new_text"] = newText
        }, ct);

    public Task<PersonalizationResult> DeleteMemoryEntryAsync(string entryId, CancellationToken ct = default) =>
        WriteAsync(new JsonObject
        {
            ["action"] = "delete_memory_entry",
            ["entry_id"] = entryId
        }, ct);

    /// <param name="rating">Null revokes the rating (sends <c>clear</c>).</param>
    public Task<PersonalizationResult> RateMemoryEntryAsync(
        string entryId, MemoryRating? rating, CancellationToken ct = default) =>
        WriteAsync(new JsonObject
        {
            ["action"] = "rate_memory_entry",
            ["entry_id"] = entryId,
            ["rating"] = rating.HasValue ? MemoryRatings.Wire(rating.Value) : MemoryRatings.ClearWire
        }, ct);

    public Task<PersonalizationResult> DeleteAllMemoriesAsync(CancellationToken ct = default) =>
        WriteAsync(new JsonObject { ["action"] = "delete_all_memories" }, ct);

    public async Task<bool> TriggerEvolutionAsync(CancellationToken ct = default) =>
        (await WriteAsync(new JsonObject { ["action"] = "trigger_user_evolution" }, ct).ConfigureAwait(false)).Succeeded;

    public async Task<IReadOnlyList<MemoryCandidate>?> GetCandidatesAsync(CancellationToken ct = default)
    {
        var resp = await PostAsync(new JsonObject { ["action"] = "get_candidates" }, ct).ConfigureAwait(false);
        if (resp is null || !IsSuccess(resp)) return null;
        if (resp["candidates"] is not JsonArray arr) return [];
        return arr
            .Select(el => el is JsonObject o ? ParseCandidate(o) : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
    }

    /// <param name="suppress">True (ignore): tombstone, never suggested again.
    /// False: merely mark handled — the actual insert goes via
    /// <see cref="AddMemoryEntryAsync"/>.</param>
    public Task<PersonalizationResult> DismissCandidateAsync(
        string candidateId, bool suppress, CancellationToken ct = default) =>
        WriteAsync(new JsonObject
        {
            ["action"] = "dismiss_candidate",
            ["candidate_id"] = candidateId,
            ["suppress"] = suppress
        }, ct);

    public async Task<StylePreferences?> GetStylePreferencesAsync(CancellationToken ct = default)
    {
        var resp = await PostAsync(new JsonObject { ["action"] = "get_style_preferences" }, ct).ConfigureAwait(false);
        if (resp is null || !IsSuccess(resp)) return null;
        if (resp["preferences"] is not JsonObject prefs) return new StylePreferences();
        var styles = (prefs["styles"] as JsonArray)?
            .Select(el => el?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList() ?? [];
        return new StylePreferences(styles, prefs["custom_instruction"]?.GetValue<string>() ?? "");
    }

    public Task<PersonalizationResult> UpdateStylePreferencesAsync(
        StylePreferences prefs, CancellationToken ct = default)
    {
        var styles = new JsonArray();
        foreach (var s in prefs.Styles) styles.Add(s);
        return WriteAsync(new JsonObject
        {
            ["action"] = "update_style_preferences",
            ["preferences"] = new JsonObject
            {
                ["styles"] = styles,
                ["custom_instruction"] = prefs.CustomInstruction
            }
        }, ct);
    }

    // -- parsing --

    private static MemoryEntry ParseEntry(JsonObject obj, int index)
    {
        var sources = (obj["sources"] as JsonArray)?
            .Select(el => el as JsonObject)
            .Where(o => o is not null && !string.IsNullOrWhiteSpace(o["chat_id"]?.GetValue<string>()))
            .Select(o => new MemorySource(GetLong(o!["ts"]), o!["chat_id"]!.GetValue<string>()))
            .ToList() ?? [];
        return new MemoryEntry(
            Id: NonBlank(obj["id"]?.GetValue<string>()) ?? index.ToString(),
            Text: obj["text"]?.GetValue<string>() ?? "",
            Section: obj["section"]?.GetValue<string>(),
            Category: obj["category"]?.GetValue<string>(),
            Confidence: GetDouble(obj["confidence"]),
            Permanent: GetBool(obj["permanent"]),
            HalfLifeDays: GetDoubleOrNull(obj["half_life_days"]),
            Ttl: GetLongOrNull(obj["ttl"]),
            UserRating: MemoryRatings.FromWire(obj["user_rating"]?.GetValue<string>()),
            FirstTs: GetLong(obj["first_ts"]),
            LastTs: GetLong(obj["last_ts"]),
            Recurrence: Math.Max(1, GetInt(obj["n_recurrence"])),
            CreatedTs: GetLong(obj["created_ts"]),
            UserSet: GetBool(obj["user_set"]),
            Sources: sources);
    }

    private static MemoryCandidate? ParseCandidate(JsonObject obj)
    {
        var id = NonBlank(obj["id"]?.GetValue<string>());
        var text = NonBlank(obj["text"]?.GetValue<string>());
        if (id is null || text is null) return null;
        return new MemoryCandidate(
            Id: id,
            Text: text,
            Quote: NonBlank(obj["quote"]?.GetValue<string>()),
            SourceChatId: NonBlank(obj["source_chat_id"]?.GetValue<string>()),
            ObservedTs: GetLong(obj["observed_ts"]),
            Section: MemorySections.FromWire(obj["section"]?.GetValue<string>()));
    }

    private static MemoryProjection ParseProjection(JsonObject obj) => new(
        Entries: GetInt(obj["entries"]),
        Skipped: GetInt(obj["skipped"]),
        Tokens: GetInt(obj["tokens"]),
        Budget: GetInt(obj["budget"]));

    // -- tolerant value readers: PHP PDO returns numeric columns as strings --

    internal static bool GetBool(JsonNode? node, bool fallback = false)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<int>(out var i)) return i != 0;
            if (v.TryGetValue<long>(out var l)) return l != 0;
            if (v.TryGetValue<double>(out var d)) return d != 0;
            if (v.TryGetValue<string>(out var s))
            {
                s = s.Trim();
                return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        return fallback;
    }

    internal static long GetLong(JsonNode? node) => GetLongOrNull(node) ?? 0;

    internal static long? GetLongOrNull(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(
                    s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return (long)parsed;
        }
        return null;
    }

    internal static double GetDouble(JsonNode? node) => GetDoubleOrNull(node) ?? 0;

    internal static double? GetDoubleOrNull(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && double.TryParse(
                    s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    internal static int GetInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)Math.Min(l, int.MaxValue);
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(
                    s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return (int)parsed;
        }
        return 0;
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsSuccess(JsonObject obj) =>
        obj["success"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;

    private async Task<PersonalizationResult> WriteAsync(JsonObject body, CancellationToken ct)
    {
        var resp = await PostAsync(body, ct).ConfigureAwait(false);
        if (resp is null) return PersonalizationResult.Fail(null);
        if (IsSuccess(resp)) return PersonalizationResult.Ok;
        var message = NonBlank(resp["message"]?.GetValue<string>());
        if (message is null && resp["expired"] is JsonValue e && e.TryGetValue<bool>(out var expired) && expired)
        {
            _auth.Logout();
            return PersonalizationResult.Fail("登录已过期，请重新登录");
        }
        return PersonalizationResult.Fail(message);
    }

    /// <summary>
    /// One POST, parsed as a JSON object. Non-2xx is NOT an early return: the
    /// guardrail 422 / toggle-off 403 both carry the useful <c>message</c> body
    /// the user most needs to see. Callers decide via <c>success</c>.
    /// </summary>
    private async Task<JsonObject?> PostAsync(JsonObject body, CancellationToken ct)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;
        try
        {
            var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(_baseUrl, "MolaGPT 个性化"));
            var url = NetworkSecurity.RequireHttps(new Uri(baseUri, RelativePath), "MolaGPT 个性化");
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _auth.Logout();
                return new JsonObject { ["success"] = false, ["expired"] = true };
            }
            var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token).ConfigureAwait(false);
            return root;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
