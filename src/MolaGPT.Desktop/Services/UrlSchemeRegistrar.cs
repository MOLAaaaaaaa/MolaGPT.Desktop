using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Registers and removes the molagpt:// custom URL scheme under HKCU
/// (no admin needed). The scheme is what oauth_landing.html jumps to
/// once the third-party login finishes — Windows then launches (or
/// hands off to) MolaGPT.Desktop.exe with the URL as argv[0].
/// </summary>
public static class UrlSchemeRegistrar
{
    public const string Scheme = "molagpt";

    public static void EnsureRegistered()
    {
        try
        {
            var exePath = GetExecutablePath();
            DiagnosticLog.Write("UrlScheme", $"EnsureRegistered exe={exePath}");
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                DiagnosticLog.Write("UrlScheme", "exe path unresolved — skipping");
                return;
            }

            // HKCU\Software\Classes\molagpt
            //   (Default) = "URL:MolaGPT Desktop"
            //   URL Protocol = ""
            //   shell\open\command\(Default) = "<exe>" "%1"
            using var classes = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{Scheme}", writable: true);
            if (classes is null) return;

            var existing = classes.GetValue(string.Empty) as string;
            var existingCmd = (Registry.CurrentUser
                .OpenSubKey($"Software\\Classes\\{Scheme}\\shell\\open\\command")
                ?.GetValue(string.Empty) as string) ?? string.Empty;
            var desiredCmd = $"\"{exePath}\" \"%1\"";
            if (existing == "URL:MolaGPT Desktop"
                && string.Equals(existingCmd, desiredCmd, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write("UrlScheme", $"already up-to-date cmd={existingCmd}");
                return;
            }

            classes.SetValue(string.Empty, "URL:MolaGPT Desktop");
            classes.SetValue("URL Protocol", string.Empty);

            using var defaultIcon = classes.CreateSubKey("DefaultIcon", writable: true);
            defaultIcon?.SetValue(string.Empty, $"\"{exePath}\",0");

            using var command = classes.CreateSubKey("shell\\open\\command", writable: true);
            command?.SetValue(string.Empty, desiredCmd);
            DiagnosticLog.Write("UrlScheme", $"registered cmd={desiredCmd}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("UrlScheme", $"register failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetExecutablePath()
    {
        // Process.MainModule.FileName resolves to the actual exe even
        // when the entrypoint dll is loaded by dotnet.exe (Debug F5).
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        return path ?? Environment.ProcessPath ?? string.Empty;
    }
}
