using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// One-shot version check across two sources, taking whichever
/// advertises the higher version:
///
///   1. GitHub Releases API ("latest") — auto-follows any release you
///      publish, no extra step. Best for users who can reach GitHub.
///   2. A small JSON manifest on the MolaGPT server — reachable from
///      networks where api.github.com is throttled/blocked (the site
///      sits behind a China-friendly Cloudflare route). You keep this
///      file in sync (the publish script can write it).
///
/// Querying both and taking the max means: publish a GitHub release and
/// GitHub-reachable users see it instantly; users who can't reach
/// GitHub fall back to the server manifest; if either source is down,
/// the other still works.
///
/// The chip opens a dialog with the release notes when GitHub is reachable,
/// or a short prompt to view the GitHub release page when only the fallback
/// manifest is reachable. GitHub-backed updates can download and verify the
/// setup asset before running it after the app exits.
/// </summary>
public sealed class UpdateCheckService
{
    // GitHub's "latest" excludes drafts and prereleases automatically.
    public const string DefaultReleasesApiUrl =
        "https://api.github.com/repos/MOLAaaaaaaa/MolaGPT.Desktop/releases/latest";

    public const string DefaultManifestUrl =
        "https://chatgpt.wljay.cn/v2/desktop-version.json";

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _manifestUrl;

    public UpdateCheckService(HttpClient http, string? apiUrl = null, string? manifestUrl = null)
    {
        _http = http;
        _apiUrl = string.IsNullOrEmpty(apiUrl) ? DefaultReleasesApiUrl : apiUrl!;
        _manifestUrl = string.IsNullOrEmpty(manifestUrl) ? DefaultManifestUrl : manifestUrl!;
    }

    public sealed record UpdateInfo(
        string LatestVersion,
        string? DownloadUrl,
        string? Notes,
        string ActionText,
        string? InstallerSha256);

    public static string CurrentDisplayVersion
    {
        get
        {
            var version = GetCurrentVersion();
            return version is null ? "unknown" : version.ToString(3);
        }
    }

    /// <summary>
    /// Queries both sources concurrently and returns the one with the
    /// highest version, but only when it is strictly newer than the
    /// running assembly. Returns null when neither source advertises a
    /// newer build (or both are unreachable). Per-source failures are
    /// logged and treated as "no info".
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var current = GetCurrentVersion();
        if (current is null)
        {
            DiagnosticLog.Write("UpdateCheck", "could not resolve current version");
            return null;
        }

        var best = await FetchBestCandidateAsync(ct).ConfigureAwait(false);

        if (best is null)
        {
            DiagnosticLog.Write("UpdateCheck", "no source returned a candidate");
            return null;
        }
        if (best.Version.CompareTo(current) <= 0)
        {
            DiagnosticLog.Write("UpdateCheck", $"up-to-date current={current} best={best.Version}");
            return null;
        }

        DiagnosticLog.Write("UpdateCheck", $"update {current} → {best.Version} via {best.Source}");
        return new UpdateInfo(
            best.DisplayVersion, best.DownloadUrl, best.Notes, best.ActionText, best.InstallerSha256);
    }

    public async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        var best = await FetchBestCandidateAsync(ct).ConfigureAwait(false);
        return best is null
            ? null
            : new UpdateInfo(
                best.DisplayVersion, best.DownloadUrl, best.Notes, best.ActionText, best.InstallerSha256);
    }

    private sealed record Candidate(
        Version Version,
        string DisplayVersion,
        string? DownloadUrl,
        string? Notes,
        string ActionText,
        string? InstallerSha256,
        string Source);

    private async Task<Candidate?> FetchBestCandidateAsync(CancellationToken ct)
    {
        var github = FetchGitHubAsync(ct);
        var manifest = FetchManifestAsync(ct);
        var results = await Task.WhenAll(github, manifest).ConfigureAwait(false);

        Candidate? best = null;
        foreach (var c in results)
        {
            if (c is null) continue;
            if (best is null || c.Version.CompareTo(best.Version) > 0)
                best = c;
        }

        return best;
    }

    private async Task<Candidate?> FetchGitHubAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
            req.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticLog.Write("UpdateCheck", $"github HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            var ver = TryParseVersion(release?.TagName);
            if (release is null || ver is null)
            {
                DiagnosticLog.Write("UpdateCheck", $"github parse failed tag={release?.TagName}");
                return null;
            }
            var installer = PickInstallerAsset(release);
            var url = installer?.DownloadUrl ?? release.HtmlUrl;
            var sha256 = NormalizeSha256Digest(installer?.Digest);
            return new Candidate(
                ver,
                release.TagName!.TrimStart('v', 'V'),
                url,
                string.IsNullOrWhiteSpace(release.Body) ? null : release.Body!.Trim(),
                sha256 is null ? "立即下载" : "下载并安装",
                sha256,
                "github");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("UpdateCheck", $"github failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<Candidate?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticLog.Write("UpdateCheck", $"manifest HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<Manifest>(json);
            var ver = TryParseVersion(manifest?.Version);
            if (manifest is null || ver is null)
            {
                DiagnosticLog.Write("UpdateCheck", $"manifest parse failed ver={manifest?.Version}");
                return null;
            }
            return new Candidate(
                ver,
                manifest.Version!.TrimStart('v', 'V'),
                string.IsNullOrWhiteSpace(manifest.Url) ? BuildReleasePageUrl(manifest.Version) : manifest.Url,
                "当前网络无法获取 GitHub Release 详情，更新内容请前往 GitHub Release 页面查看。",
                "查看 Release",
                null,
                "manifest");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("UpdateCheck", $"manifest failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prefers the Inno Setup installer asset (the *-setup.exe). Falls
    /// back to the first .exe, then to null so the caller can use the
    /// release HTML page instead.
    /// </summary>
    private static GitHubAsset? PickInstallerAsset(GitHubRelease release)
    {
        if (release.Assets is null || release.Assets.Count == 0) return null;
        var setup = release.Assets.FirstOrDefault(a =>
            a.Name?.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase) == true);
        if (setup?.DownloadUrl is not null) return setup;
        return release.Assets.FirstOrDefault(a =>
            a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
            && a.DownloadUrl is not null);
    }

    private static Version? GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateCheckService).Assembly;
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

    private static string BuildReleasePageUrl(string? version)
    {
        var tag = string.IsNullOrWhiteSpace(version) ? string.Empty : version.Trim();
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            tag = $"v{tag}";
        return $"https://github.com/MOLAaaaaaaa/MolaGPT.Desktop/releases/tag/{tag}";
    }

    private static string? NormalizeSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];
        return value.Length == 64 && value.All(IsHex) ? value.ToLowerInvariant() : null;

        static bool IsHex(char c) =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    private sealed class Manifest
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}

