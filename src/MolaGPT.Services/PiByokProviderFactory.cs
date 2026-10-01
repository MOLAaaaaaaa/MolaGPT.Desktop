using System.Net.Http;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Builds the agent-runtime provider for a saved BYOK row.
///
/// Returning null means the row cannot be carried at all — an unknown wire shape,
/// a missing key, or no runtime on this machine. There is no direct provider left
/// to fall back to, so the caller leaves the row unregistered and says why.
/// </summary>
public sealed class PiByokProviderFactory
{
    private sealed record Shape(
        string Api,
        string DefaultPath,
        PiWorkLlmShim.AuthStyle Auth,
        bool AuthHeader = true,
        PiWorkLlmShim.TargetPathMode PathMode = PiWorkLlmShim.TargetPathMode.Fixed);

    /// <summary>
    /// Provider row types the shim can carry, and how each is reached.
    ///
    /// <c>gemini</c> takes Google's <b>native</b> API rather than the
    /// OpenAI-compatible endpoint its rows are configured against, even though the
    /// compatible one looks like a free win. That endpoint drops the
    /// <c>thought_signature</c> Gemini 3 requires echoed back on function-call
    /// parts: verified end-to-end, the first tool round succeeds and the second is
    /// rejected with "Function call is missing a thought_signature in functionCall
    /// parts". Work always sends tools, so every multi-step task would fail on its
    /// second step. The native API preserves the signature.
    /// </summary>
    private static readonly Dictionary<string, Shape> Eligible =
        new(StringComparer.Ordinal)
        {
            ["openai-compat"] = new("openai-completions", "", PiWorkLlmShim.AuthStyle.Bearer),
            ["anthropic"] = new("anthropic-messages", "v1/messages", PiWorkLlmShim.AuthStyle.AnthropicApiKey),
            ["openai-response"] = new("openai-responses", DefaultResponsesPath, PiWorkLlmShim.AuthStyle.Bearer),

            // Google puts the model id and the operation in the path, so the shim
            // forwards the suffix rather than a fixed URL, and authenticates with
            // x-goog-api-key — a bearer is read as an OAuth token and refused.
            ["gemini"] = new(
                "google-generative-ai",
                "",
                PiWorkLlmShim.AuthStyle.GoogleApiKey,
                AuthHeader: false,
                PathMode: PiWorkLlmShim.TargetPathMode.AppendInboundSuffix),

            // A ChatGPT plan, through either sign-in (see ChatGptAccount). No base URL
            // or key of its own — both come from the signed-in account, per turn.
            ["chatgpt"] = new("openai-responses", "responses", PiWorkLlmShim.AuthStyle.Bearer),
        };

    /// <summary>
    /// Fields a ChatGPT plan request must not carry; OpenAI rejects the request
    /// over any of them. Pi's own OpenAI provider omits the same set when it sees a
    /// ChatGPT token, but behind the shim it cannot see one. The list is the
    /// official flow's; the Codex backend was checked against it and refuses
    /// <c>max_output_tokens</c>, <c>temperature</c> and <c>prompt_cache_retention</c>
    /// the same way.
    /// </summary>
    private static readonly string[] ChatGptPlanDrops =
    [
        "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation",
        "multi_agent", "prompt", "prompt_cache_retention", "prompt_cache_options", "safety_identifier",
        "temperature", "top_logprobs", "top_p", "truncation", "user"
    ];

    /// <summary>Wire defaults for rows that leave the api path blank.</summary>
    private const string DefaultChatPath = "v1/chat/completions";
    private const string DefaultResponsesPath = "v1/responses";

    /// <summary>Google's native generative-language root. A <c>gemini</c> row's own
    /// base URL points at the OpenAI-compatibility layer under it, which is not the
    /// API we drive, so it is deliberately not reused here.</summary>
    private const string GeminiNativeBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly PiWorkSidecarLocator _locator;
    private readonly IChatToolHost _toolHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PiRuntime _runtime;
    private readonly Action<string>? _log;
    private readonly ChatGptAccount? _chatGpt;

    /// <summary>The account ChatGPT rows draw on; the settings page signs it in.</summary>
    public ChatGptAccount? ChatGpt => _chatGpt;

    /// <summary>Live Pi providers by provider id, so re-registering one (a settings
    /// save) tears down the sidecars belonging to the copy it replaces.</summary>
    private readonly Dictionary<string, PiWorkProvider> _active = new(StringComparer.Ordinal);

    public PiByokProviderFactory(
        PiWorkSidecarLocator locator,
        IChatToolHost toolHost,
        IHttpClientFactory httpClientFactory,
        PiRuntime runtime,
        Action<string>? log = null,
        ChatGptAccount? chatGpt = null)
    {
        _chatGpt = chatGpt;
        _locator = locator;
        _toolHost = toolHost;
        _httpClientFactory = httpClientFactory;
        _runtime = runtime;
        _log = log;
    }

    /// <summary>
    /// Whether this machine currently has a runtime any row could be wrapped onto.
    ///
    /// Split out from <see cref="TryWrap"/> because null has two very different
    /// meanings for the user: "this particular row cannot be carried" is about the
    /// row, while "there is no compatible runtime here" is about the machine and
    /// takes every row down with it. Only the second one has a fix the user can
    /// act on, so callers must be able to tell them apart before choosing words.
    /// </summary>
    public bool IsRuntimeAvailable
    {
        get
        {
            try { return _locator.TryResolve() is not null; }
            catch (Exception ex)
            {
                _log?.Invoke("[pi-byok] 定位 Agent 运行时失败：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Returns the provider for this row, or null when it cannot be
    /// carried — in which case the row stays out of the picker.</summary>
    public IChatProvider? TryWrap(
        string type,
        string id,
        string displayName,
        string? baseUrl,
        string? apiPath,
        string? apiKey,
        IReadOnlyList<ProviderModel> models,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        if (!Eligible.TryGetValue(type, out var shape)) return null;
        var chatGpt = type.Equals("chatgpt", StringComparison.Ordinal);
        if (chatGpt)
        {
            // Registered whether or not anyone is signed in right now: the row is
            // the user's choice, and a turn on it says how to sign in rather than
            // the model silently vanishing from the picker.
            if (_chatGpt is null) return null;
            baseUrl = ChatGptAccount.ApiBase;
            apiPath = null;
            apiKey = "-";
        }
        else if (type.Equals("gemini", StringComparison.Ordinal))
        {
            // Saved Gemini rows use Google's OpenAI-compatible root so the settings
            // page can list models. Pi speaks the native protocol and must never
            // inherit that address.
            baseUrl = GeminiNativeBaseUrl;
        }
        // No default for a blank base URL: the settings page refuses to save a row
        // without one, so a blank here means the row is corrupt, not unconfigured,
        // and guessing an endpoint for it would just point the key somewhere the
        // user never named.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;
        if (models.Count == 0) return null;

        PiSidecarAssets? assets;
        try { assets = _locator.TryResolve(); }
        catch (Exception ex)
        {
            _log?.Invoke("[pi-byok] 定位 Agent 运行时失败：" + ex.Message);
            return null;
        }
        if (assets is null) return null;

        string endpoint;
        try
        {
            if (shape.PathMode == PiWorkLlmShim.TargetPathMode.AppendInboundSuffix)
            {
                // The path is the request here, so the endpoint is only a root and a
                // configured api path would be meaningless against it.
                endpoint = baseUrl!.TrimEnd('/');
            }
            else
            {
                var root = baseUrl!.TrimEnd('/') + "/";
                var fallback = shape.DefaultPath.Length == 0 ? DefaultChatPath : shape.DefaultPath;
                var relative = string.IsNullOrWhiteSpace(apiPath) ? fallback : apiPath!.Trim();
                endpoint = new Uri(new Uri(root), relative).ToString();
            }
        }
        catch (UriFormatException ex)
        {
            _log?.Invoke("[pi-byok] 端点无法解析：" + ex.Message);
            return null;
        }

        var key = apiKey!;
        var account = chatGpt ? _chatGpt : null;
        Func<CancellationToken, Task<string?>> token = account is null
            ? _ => Task.FromResult<string?>(key)
            : async ct => await account.GetAccessTokenAsync(ct).ConfigureAwait(false)
                          ?? throw new PiWorkLlmShim.CredentialUnavailableException(
                              account.LastError ?? "未登录 ChatGPT，请在「设置 → 模型服务」中登录。");
        var config = new PiWorkProviderConfig(
            id,
            displayName,
            models,
            new PiSidecarSpec(
                id,
                assets.NodePath,
                assets.CliJsPath,
                assets.ExtensionPath,
                PiWorkSidecarLocator.SessionRoot,
                PiWorkSidecarLocator.SessionRoot,
                PiModelCatalog.BuildJson(models, shape.Api, displayName, endpoint),
                models[0].Id,
                shape.Api,
                shape.AuthHeader),
            // A ChatGPT row follows whichever sign-in the account holds at the
            // start of the turn: the two reach different endpoints.
            request => new PiProviderCreds(
                account?.Endpoint ?? endpoint,
                token,
                request.ModelId,
                Api: shape.Api,
                // OpenAI refusing the token means the grant is gone; the next turn
                // should ask for a sign-in, not fail the same way.
                OnUnauthorized: account is null ? null : () => account.Expire("ChatGPT 登录已失效，请重新登录。"),
                Auth: shape.Auth,
                Headers: account?.RequestHeaders() ?? headers,
                // Custom parameters are per model, so they are resolved per turn
                // rather than baked in when the provider is built.
                ExtraBody: models.FirstOrDefault(m =>
                    m.Id.Equals(request.ModelId, StringComparison.OrdinalIgnoreCase))?.CustomBody,
                DropBodyKeys: account is null ? null : ChatGptPlanDrops,
                PathMode: shape.PathMode,
                StreamOnly: account is not null,
                AdaptBody: account?.Kind is { } kind ? body => ChatGptAccount.AdaptBody(body, kind) : null,
                ResponseReceived: account is null ? null : account.ReadUsage));

        var provider = new PiWorkProvider(
            config,
            _toolHost,
            _httpClientFactory.CreateClient(HttpClientNames.Byok),
            _runtime,
            _log);

        Retire(id);
        _active[id] = provider;
        return provider;
    }

    /// <summary>Dispose the Pi provider previously registered under this id, if any.
    /// Fire-and-forget: teardown must never block a registration.</summary>
    public void Retire(string id)
    {
        if (!_active.Remove(id, out var previous)) return;
        _ = Task.Run(async () =>
        {
            try { await previous.DisposeAsync(); }
            catch (Exception ex) { _log?.Invoke("[pi-byok] 释放 sidecar 失败：" + ex.Message); }
        });
    }
}
