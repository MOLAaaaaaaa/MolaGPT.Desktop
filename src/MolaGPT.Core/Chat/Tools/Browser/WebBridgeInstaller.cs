using System.Diagnostics;
using System.Text;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// Runs Kimi's own bootstrap installer for the local bridge service.
///
/// Kimi ships no MSI: the documented path is a PowerShell one-liner that
/// downloads <c>kimi-webbridge.exe</c> into the user profile and starts it. We
/// run that same script rather than reimplementing it, so an upstream change to
/// the layout or the release channel does not silently leave MolaGPT installing
/// the wrong thing.
/// </summary>
public static class WebBridgeInstaller
{
    public const string ScriptUrl = "https://cdn.kimi.com/webbridge/install.ps1";

    /// <summary>The command as Kimi documents it, for display and copying.</summary>
    public const string DisplayCommand = "irm https://cdn.kimi.com/webbridge/install.ps1 | iex";

    /// <summary>
    /// What we actually run. <c>-NoSkill</c> is the one deviation from the
    /// documented line: without it the script also writes Kimi's skill files
    /// into every AI-agent runtime it finds on the machine (Claude Code, Codex,
    /// Cursor …). Installing a browser bridge for MolaGPT is not consent to edit
    /// the user's other tools, and MolaGPT does not read those files anyway.
    /// </summary>
    public static string BuildCommand() =>
        $"iex \"& {{ $(irm {ScriptUrl}) }} -NoSkill\"";

    /// <summary>
    /// Runs the installer, reporting the script's own output lines as they
    /// arrive. Returns true when the binary is on disk afterwards — the script
    /// warns rather than fails on a daemon that will not start, so its exit code
    /// alone would report success for a half-done install.
    /// </summary>
    public static async Task<WebBridgeInstallResult> RunAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var log = new StringBuilder();

        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            log.AppendLine(line);
            progress?.Report(line.Trim());
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(BuildCommand());

            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);

            if (!process.Start())
                return new WebBridgeInstallResult(false, "无法启动 PowerShell。");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new WebBridgeInstallResult(false, "配置超时。", log.ToString());
            }

            var installed = WebBridgeAddress.IsInstalled;
            return installed
                ? new WebBridgeInstallResult(true, null, log.ToString())
                : new WebBridgeInstallResult(false, "安装脚本已结束，但未找到本地服务。", log.ToString());
        }
        catch (Exception ex)
        {
            return new WebBridgeInstallResult(false, ex.Message, log.ToString());
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}

public sealed record WebBridgeInstallResult(bool Success, string? Error = null, string? Log = null);
