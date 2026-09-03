using System.Net.Http;
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
        };

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

    /// <summary>Live Pi providers by provider id, so re-registering one (a settings
    /// save) tears down the sidecars belonging to the copy it replaces.</summary>
    private readonly Dictionary<string, PiWorkProvider> _active = new(StringComparer.Ordinal);

    public PiByokProviderFactory(
        PiWorkSidecarLocator locator,
        IChatToolHost toolHost,
        IHttpClientFactory httpClientFactory,
        PiRuntime runtime,
        Action<string>? log = null)
    {
        _locator = locator;
        _toolHost = toolHost;
        _httpClientFactory = httpClientFactory;
        _runtime = runtime;
        _log = log;
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
        if (type.Equals("gemini", StringComparison.Ordinal))
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
                shape.AuthHeader,
                Reasoning: models.Any(m => m.SupportsThinking)),
            request => new PiProviderCreds(
                endpoint,
                _ => Task.FromResult<string?>(key),
                request.ModelId,
                Api: shape.Api,
                Auth: shape.Auth,
                Headers: headers,
                // Custom parameters are per model, so they are resolved per turn
                // rather than baked in when the provider is built.
                ExtraBody: models.FirstOrDefault(m =>
                    m.Id.Equals(request.ModelId, StringComparison.OrdinalIgnoreCase))?.CustomBody,
                PathMode: shape.PathMode));

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
