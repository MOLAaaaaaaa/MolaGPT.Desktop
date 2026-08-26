using System.Net.Http;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Optionally re-hosts a BYOK provider on the Pi harness, keeping the provider id
/// so the rest of the app is unaffected.
///
/// Separate opt-in from Work (<c>pi.byok.enabled</c>, default off) because BYOK is
/// the default mode and the Pi path is not yet feature-equivalent: the provider
/// hands Pi only the latest user turn, so history-shaping features on earlier
/// messages do not apply. Eligibility is per wire shape: OpenAI-compatible and
/// Anthropic are carried; Responses and Gemini stay on the direct provider, since
/// the shim speaks those two shapes only.
/// </summary>
public sealed class PiByokProviderFactory
{
    public const string KeyEnabled = "pi.byok.enabled";

    /// <summary>Provider row types the shim can carry, and how each authenticates.
    /// Responses and Gemini are absent on purpose: the shim speaks these two wire
    /// shapes only, and guessing at a third would fail at request time rather than
    /// here, where falling back to the direct provider is free.</summary>
    private static readonly Dictionary<string, (string Api, string DefaultPath, PiWorkLlmShim.AuthStyle Auth)> Eligible =
        new(StringComparer.Ordinal)
        {
            ["openai-compat"] = ("openai-completions", "", PiWorkLlmShim.AuthStyle.Bearer),
            ["anthropic"] = ("anthropic-messages", "v1/messages", PiWorkLlmShim.AuthStyle.AnthropicApiKey),
        };

    private readonly SettingsRepository _settings;
    private readonly PiWorkSidecarLocator _locator;
    private readonly IChatToolHost _toolHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Action<string>? _log;

    /// <summary>Live Pi providers by provider id, so re-registering one (a settings
    /// save) tears down the sidecars belonging to the copy it replaces.</summary>
    private readonly Dictionary<string, PiWorkProvider> _active = new(StringComparer.Ordinal);

    public PiByokProviderFactory(
        SettingsRepository settings,
        PiWorkSidecarLocator locator,
        IChatToolHost toolHost,
        IHttpClientFactory httpClientFactory,
        Action<string>? log = null)
    {
        _settings = settings;
        _locator = locator;
        _toolHost = toolHost;
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public bool Enabled =>
        string.Equals(_settings.Get(KeyEnabled), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a Pi-backed provider for this row, or null when the caller should
    /// register the direct provider it already built.
    /// </summary>
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
        if (!Enabled) return null;
        if (!Eligible.TryGetValue(type, out var shape)) return null;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;
        if (models.Count == 0) return null;

        PiSidecarAssets? assets;
        try { assets = _locator.TryResolve(); }
        catch (Exception ex)
        {
            _log?.Invoke("[pi-byok] 定位 sidecar 失败，沿用直连：" + ex.Message);
            return null;
        }
        if (assets is null) return null;

        string endpoint;
        try
        {
            var root = baseUrl!.TrimEnd('/') + "/";
            var relative = shape.DefaultPath.Length == 0
                ? OpenAICompatibleProvider.ResolveChatPath(apiPath)
                : (string.IsNullOrWhiteSpace(apiPath) ? shape.DefaultPath : apiPath!.Trim());
            endpoint = new Uri(new Uri(root), relative).ToString();
        }
        catch (UriFormatException ex)
        {
            _log?.Invoke("[pi-byok] 端点无法解析，沿用直连：" + ex.Message);
            return null;
        }

        var key = apiKey!;
        var config = new PiWorkProviderConfig(
            id,
            displayName,
            models,
            assets.NodePath,
            assets.CliJsPath,
            assets.ExtensionPath,
            assets.WorkingDirectory,
            PiWorkSidecarLocator.SessionRoot,
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
                    m.Id.Equals(request.ModelId, StringComparison.OrdinalIgnoreCase))?.CustomBody));

        var provider = new PiWorkProvider(
            config,
            _toolHost,
            _httpClientFactory.CreateClient(HttpClientNames.Byok),
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
