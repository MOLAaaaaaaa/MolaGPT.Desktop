using System.Diagnostics;

namespace MolaGPT.App.Rendering;

/// <summary>
/// The one exit for a link that came out of a model.
///
/// Source chips, markdown links in a paragraph, links inside a table cell or a
/// list item — all of them end up here, so the policy is written once: http and
/// https only, handed to the system browser, never navigated inside the window.
///
/// The scheme check is the point. Transcript text is untrusted, the artifact
/// rewriter turns relative links into absolute local paths, and
/// <see cref="ProcessStartInfo.UseShellExecute"/> on a local path is an open —
/// which for the wrong extension is a run. Refusing everything that is not web
/// keeps "[点这里](…)" from being a one-click anything.
/// </summary>
internal static class LinkLauncher
{
    /// <summary>
    /// Whether this URL is one we would actually open. The renderer asks first,
    /// so a link it cannot honour is never given a hand cursor: a link that
    /// looks live and does nothing is worse than one that looks like text.
    /// </summary>
    public static bool CanOpen(string? url) => Parse(url) is not null;

    public static void Open(string? url)
    {
        if (Parse(url) is not { } uri) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // No default browser, or the shell refused; not worth a dialog.
        }
    }

    private static Uri? Parse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : null;
}
