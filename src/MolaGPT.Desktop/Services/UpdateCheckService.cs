using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// One-shot version check against a manifest hosted on the MolaGPT
/// server. The manifest is a small JSON file (`desktop-version.json`)
/// with the latest released version, an absolute URL to the Inno
/// Setup installer (which, per Inno Setup convention, is safe to
/// install over the existing program — it overwrites files in place
/// and preserves %LocalAppData%\MolaGPT data), and an optional
/// changelog string.
///
/// We don't auto-download or auto-run the installer. The user clicks
/// the chip, the URL opens in the browser, and they execute it
/// themselves. That keeps the trust surface small and matches what
/// most Windows apps do for non-MSIX distributions.
/// </summary>
public sealed class UpdateCheckService
{
    public const string DefaultManifestUrl = "https://chatgpt.wljay.cn/v2/desktop-version.json";

    private readonly HttpClient _http;
    private readonly string _manifestUrl;

    public UpdateCheckService(HttpClient http, string? manifestUrl = null)
    {
        _http = http;
        _manifestUrl = string.IsNullOrEmpty(manifestUrl) ? DefaultManifestUrl : manifestUrl!;
    }

    public sealed record UpdateInfo(string LatestVersion, string? DownloadUrl, string? Notes);

    /// <summary>
    /// Returns a populated <see cref="UpdateInfo"/> when the manifest
    /// advertises a strictly newer version than the running assembly,
    /// otherwise null. Network failures and malformed manifests return
    /// null silently — the chip simply stays hidden.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<Manifest>(json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version)) return null;

            var latest = TryParseVersion(manifest.Version);
            var current = GetCurrentVersion();
            if (latest is null || current is null) return null;
            if (latest.CompareTo(current) <= 0) return null;

            return new UpdateInfo(manifest.Version!, manifest.Url, manifest.Notes);
        }
        catch
        {
            return null;
        }
    }

    private static Version? GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateCheckService).Assembly;
        // AssemblyInformationalVersionAttribute can carry "+commit" suffixes
        // — strip them so Version.Parse succeeds.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParseVersion(info) ?? asm.GetName().Version;
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
        var dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];
        return Version.TryParse(trimmed, out var v) ? v : null;
    }

    private sealed class Manifest
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }
}
