using System.IO;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Decides whether MolaGPT <b>Work</b> should run on the Pi harness, and finds the
/// pieces needed to launch it. Two independent gates, both of which must pass:
///
///  1. <see cref="Enabled"/> — an explicit opt-in flag (default <c>false</c>), so a
///     normal build keeps today's in-process Work agent and no Node ever starts.
///  2. <see cref="TryResolve"/> — the sidecar assets and a Node runtime actually
///     exist on this machine. Missing pieces mean a silent, safe fall back to the
///     existing provider rather than a broken Work mode.
///
/// Node is only ever launched from a resolved absolute path or the PATH-resolved
/// <c>node</c>; nothing here downloads or installs a runtime.
/// </summary>
public sealed class PiWorkSidecarLocator
{
    public const string KeyEnabled = "pi.work.enabled";
    public const string KeyNodePath = "pi.work.nodePath";
    public const string KeySidecarDir = "pi.work.sidecarDir";

    /// <summary>Where Pi keeps its per-conversation transcripts. Single source of
    /// truth: the provider writes here and <see cref="PiWorkSessionSweeper"/> prunes
    /// here, so the two can never drift onto different directories.</summary>
    public static string SessionRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT", "pi-sessions");

    private static readonly string[] CliRelativePath =
        { "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js" };

    private readonly SettingsRepository _settings;
    private readonly Func<InstalledPiSidecar?>? _installed;

    /// <param name="installed">Resolves the installed sandbox component. A delegate
    /// rather than the manager itself so the locator stays free of the download
    /// machinery it only needs to ask one question of.</param>
    public PiWorkSidecarLocator(SettingsRepository settings, Func<InstalledPiSidecar?>? installed = null)
    {
        _settings = settings;
        _installed = installed;
    }

    /// <summary>Opt-in: route Work through the Pi sidecar instead of the in-process
    /// agent. Off by default — flipping it changes which provider is registered on
    /// the next refresh (login / restart).</summary>
    public bool Enabled
    {
        get => string.Equals(_settings.Get(KeyEnabled), "true", StringComparison.OrdinalIgnoreCase);
        set => _settings.Set(KeyEnabled, value ? "true" : "false");
    }

    /// <summary>Locate node + the sidecar assets, or null if this machine can't run them.</summary>
    public PiSidecarAssets? TryResolve()
    {
        // The installed sandbox component wins: it ships a known-good Node and a
        // pinned Pi, so it is the configuration actually tested. The repo checkout
        // below is the developer path.
        if (_installed?.Invoke() is { } managed)
        {
            return new PiSidecarAssets(
                managed.NodePath, managed.CliJsPath, managed.ExtensionPath, managed.Directory);
        }

        var dir = ResolveSidecarDir();
        if (dir is null) return null;

        var cliJs = Path.Combine(new[] { dir }.Concat(CliRelativePath).ToArray());
        var extension = Path.Combine(dir, "molagpt-extension.ts");
        if (!File.Exists(cliJs) || !File.Exists(extension)) return null;

        var node = ResolveNodePath();
        return node is null ? null : new PiSidecarAssets(node, cliJs, extension, dir);
    }

    private string? ResolveSidecarDir()
    {
        if (Normalize(_settings.Get(KeySidecarDir)) is { } configured)
            return Directory.Exists(configured) ? configured : null;

        // Shipped layout: pi-sidecar/ sits next to the executable. Dev layout: it
        // sits at the repo root, several levels above bin/<config>/<tfm>/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "pi-sidecar");
            if (File.Exists(Path.Combine(candidate, "molagpt-extension.ts"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private string? ResolveNodePath()
    {
        if (Normalize(_settings.Get(KeyNodePath)) is { } configured)
            return File.Exists(configured) ? configured : null;

        foreach (var candidate in ProbeNodeCandidates())
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static IEnumerable<string> ProbeNodeCandidates()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string candidate;
            try { candidate = Path.Combine(entry.Trim(), "node.exe"); }
            catch { continue; } // malformed PATH entry
            yield return candidate;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "nodejs", "node.exe");
            yield return Path.Combine(root, "Programs", "nodejs", "node.exe");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Everything the launcher needs, all verified to exist.</summary>
public sealed record PiSidecarAssets(
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    string WorkingDirectory);
