using System.Net.Http;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;

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
                "models/{model}:streamGenerateContent",
                PiWorkLlmShim.AuthStyle.GoogleApiKey,
                AuthHeader: false,
                PathMode: PiWorkLlmShim.TargetPathMode.GeminiModelTemplate),

            // A ChatGPT plan through its selected sign-in. No base URL
            // or key of its own — both come from the signed-in account, per turn.
            ["chatgpt"] = new("openai-responses", "responses", PiWorkLlmShim.AuthStyle.Bearer),
        };

    /// <summary>
    /// Fields a ChatGPT plan request must not carry; OpenAI rejects the request
    /// over any of them. Pi's own OpenAI provider omits the same set when it sees a
    /// ChatGPT token, but behind the shim it cannot see one. The list is the
    /// official flow's.
    /// </summary>
    private static readonly string[] ChatGptPlanDrops =
    [
        "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation",
        "multi_agent", "prompt", "prompt_cache_retention", "prompt_cache_options", "safety_identifier",
        "temperature", "top_logprobs", "top_p", "truncation", "user", "previous_response_id"
    ];

    /// <summary>Wire defaults for rows that leave the api path blank.</summary>
    private const string DefaultChatPath = "v1/chat/completions";
    private const string DefaultResponsesPath = "v1/responses";

    private readonly PiWorkSidecarLocator _locator;
    private readonly IChatToolHost _toolHost;
    private readonly HttpClient _http;
    private readonly PiRuntime _runtime;
    private readonly Action<string>? _log;
    private readonly ChatGptAccount? _chatGpt;

    /// <summary>The account ChatGPT rows draw on; the settings page signs it in.</summary>
    public ChatGptAccount? ChatGpt => _chatGpt;

    public PiByokProviderFactory(
        PiWorkSidecarLocator locator,
        IChatToolHost toolHost,
        HttpClient http,
        PiRuntime runtime,
        Action<string>? log = null,
        ChatGptAccount? chatGpt = null)
    {
        _chatGpt = chatGpt;
        _locator = locator;
        _toolHost = toolHost;
        _http = http;
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
    public PiWorkProvider? TryWrap(
        string type,
        string id,
        string displayName,
        string? baseUrl,
        string? apiPath,
        string? apiKey,
        IReadOnlyList<ProviderModel> models,
        out ProviderApplyOutcome outcome,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        bool builtinSearch = false)
    {
        outcome = ProviderApplyOutcome.Unsupported;
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
        else if (GeminiModelPolicy.IsLegacyNativePreset(type, baseUrl, apiPath))
        {
            baseUrl = GeminiModelPolicy.NativeBaseUrl;
            apiPath = shape.DefaultPath;
        }
        // No default for a blank base URL: the settings page refuses to save a row
        // without one, so a blank here means the row is corrupt, not unconfigured,
        // and guessing an endpoint for it would just point the key somewhere the
        // user never named.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;
        if (models.Count == 0) return null;

        var assets = _locator.TryResolve();
        if (assets is null)
        {
            outcome = ProviderApplyOutcome.RuntimeUnavailable;
            return null;
        }

        string endpoint;
        try
        {
            if (shape.PathMode == PiWorkLlmShim.TargetPathMode.GeminiModelTemplate)
            {
                var path = string.IsNullOrWhiteSpace(apiPath) ? shape.DefaultPath : apiPath.Trim();
                path = path.Replace(":generateContent", ":streamGenerateContent", StringComparison.Ordinal);
                endpoint = NetworkSecurity.CombineEndpoint(baseUrl!, path, displayName).ToString();
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
            : async ct =>
            {
                var access = await account.GetAccessTokenAsync(ct).ConfigureAwait(false)
                    ?? throw new PiWorkLlmShim.CredentialUnavailableException(
                        account.LastError ?? "未登录 ChatGPT，请在「设置 → 模型服务」中登录。");
                if (!account.CanUsePlan)
                    throw new PiWorkLlmShim.CredentialUnavailableException("请在「设置 → 模型服务」中授权使用 ChatGPT 套餐。");
                return access;
            };
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
            request => new PiProviderCreds(
                account?.Endpoint ?? endpoint,
                token,
                request.ModelId,
                Api: shape.Api,
                Auth: shape.Auth,
                Headers: account is null ? headers : account.RequestHeaders(),
                // Custom parameters are per model, so they are resolved per turn
                // rather than baked in when the provider is built.
                ExtraBody: models.FirstOrDefault(m =>
                    m.Id.Equals(request.ModelId, StringComparison.OrdinalIgnoreCase))?.CustomBody,
                DropBodyKeys: account is null ? null : ChatGptPlanDrops,
                PathMode: shape.PathMode,
                StreamOnly: account is not null,
                AdaptBody: account?.Kind is { } kind ? body => ChatGptAccount.AdaptBody(body, kind) : null,
                ResponseReceived: account is null ? null : account.ObserveResponse),
            BuiltinSearch: !chatGpt && builtinSearch);

        outcome = ProviderApplyOutcome.Registered;
        return new PiWorkProvider(
            config,
            _toolHost,
            _http,
            _http,
            _runtime,
            _log);

    }
}

/// <summary>
/// Why a saved row did or did not end up in the registry.
///
/// The registry is what the model picker reads, so "saved" and "usable" are two
/// different states and the user can only see the first one. Every path that
/// registers a row reports which of these happened, so nothing can report
/// success while leaving the picker empty.
/// </summary>
public enum ProviderApplyOutcome
{
    /// <summary>In the registry, and therefore in the picker.</summary>
    Registered,

    /// <summary>Disabled, or an image row — deliberately not a chat provider.</summary>
    NotApplicable,

    /// <summary>An otherwise eligible row cannot run without a compatible Agent runtime.</summary>
    RuntimeUnavailable,

    /// <summary>The row itself cannot be carried — unknown wire shape, missing
    /// credentials, or no models. Specific to this row.</summary>
    Unsupported
}
