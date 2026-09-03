using System.IO;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Points at the installed agent runtime — the Pi harness, the MolaGPT extension,
/// and the Node that runs them.
///
/// <b>The downloaded runtime is the only source, on purpose.</b> This used to fall
/// back to a directory named in settings and then to a <c>pi-sidecar/</c> found by
/// walking up from the executable, and both were invisible to everything else: the
/// settings page reports on the download, the contract version gates the download,
/// and "移除" deletes the download. A machine resolving from anywhere else showed
/// "未下载" while Work ran fine, kept an extension that could never be judged stale,
/// and survived a delete the user had just confirmed. One source keeps all four
/// answers — status, gate, delete, launch — about the same files.
///
/// To run a modified extension, install a locally built package into that one
/// place: <c>pi-sidecar/build-package.ps1 -Install</c>.
///
/// <see cref="TryResolve"/> returning null means Work and BYOK are unavailable
/// until the runtime is downloaded. The caller is expected to say so.
/// </summary>
public sealed class PiWorkSidecarLocator
{
    /// <summary>Where Pi keeps its per-conversation transcripts. Single source of
    /// truth: the provider writes here and <see cref="PiWorkSessionSweeper"/> prunes
    /// here, so the two can never drift onto different directories.</summary>
    public static string SessionRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT", "pi-sessions");

    private readonly Func<InstalledPiSidecar?>? _installed;

    /// <param name="installed">Resolves the installed component. A delegate rather
    /// than the manager itself so the locator stays free of the download machinery
    /// it only needs to ask one question of.</param>
    public PiWorkSidecarLocator(Func<InstalledPiSidecar?>? installed = null) =>
        _installed = installed;

    /// <summary>The runtime to launch, or null when this machine has none.</summary>
    public PiSidecarAssets? TryResolve() =>
        _installed?.Invoke() is { } managed
            ? new PiSidecarAssets(
                managed.NodePath, managed.CliJsPath, managed.ExtensionPath)
            : null;
}

/// <summary>Everything the launcher needs, all verified to exist.</summary>
public sealed record PiSidecarAssets(
    string NodePath,
    string CliJsPath,
    string ExtensionPath);
