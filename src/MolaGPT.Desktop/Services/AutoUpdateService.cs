using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;

namespace MolaGPT.Desktop.Services;

public sealed class AutoUpdateService
{
    private readonly HttpClient _http;

    public AutoUpdateService(HttpClient http)
    {
        _http = http;
    }

    public sealed record UpdatePackage(
        string Version,
        string DownloadUrl,
        string Sha256,
        string FileName);

    public async Task<string> DownloadAndVerifyAsync(
        UpdatePackage package,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop",
            "Updates",
            package.Version);
        Directory.CreateDirectory(targetDir);

        var fileName = string.IsNullOrWhiteSpace(package.FileName)
            ? $"MolaGPT.Desktop-{package.Version}-win-x64-setup.exe"
            : package.FileName;
        var targetPath = Path.Combine(targetDir, fileName);
        var tempPath = targetPath + ".download";

        if (File.Exists(targetPath) && await VerifySha256Async(targetPath, package.Sha256, ct).ConfigureAwait(false))
        {
            progress?.Report(1);
            return targetPath;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
        req.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long readTotal = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;
                if (total is > 0)
                    progress?.Report(Math.Clamp((double)readTotal / total.Value, 0, 1));
            }
        }

        if (!await VerifySha256Async(tempPath, package.Sha256, ct).ConfigureAwait(false))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException("安装包校验失败，请稍后重试。");
        }

        if (File.Exists(targetPath))
            File.Delete(targetPath);
        File.Move(tempPath, targetPath);
        progress?.Report(1);
        return targetPath;
    }

    public void InstallAfterExitAndRestart(string installerPath)
    {
        StartInstallerAfterExit(installerPath, restartAfterInstall: true);
        Application.Current.Shutdown();
    }

    public void InstallAfterExitWithoutRestart(string installerPath)
        => StartInstallerAfterExit(installerPath, restartAfterInstall: false);

    private static void StartInstallerAfterExit(string installerPath, bool restartAfterInstall)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("安装包不存在。", installerPath);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("无法定位当前程序路径。");

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"molagpt-update-{Environment.ProcessId}-{Guid.NewGuid():N}.ps1");
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop",
            "Updates");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "update-installer.log");
        var script = """
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$AppExe,
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][string]$RestartAfterInstall,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = "Stop"
$restart = $RestartAfterInstall -eq "1"

function Write-UpdateLog([string]$Message) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -LiteralPath $LogPath -Value "$stamp $Message" -Encoding UTF8
}

try {
    Write-UpdateLog "waiting for parent pid $ParentPid; restart=$restart"
    Wait-Process -Id $ParentPid -ErrorAction SilentlyContinue
    Write-UpdateLog "starting installer: $Installer"
    $process = Start-Process -FilePath $Installer -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Wait -PassThru
    Write-UpdateLog "installer exit code: $($process.ExitCode)"
    if ($restart -and $process.ExitCode -eq 0 -and (Test-Path -LiteralPath $AppExe)) {
        Write-UpdateLog "restarting app: $AppExe"
        Start-Process -FilePath $AppExe
    }
} catch {
    Write-UpdateLog "failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    throw
} finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
        File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Installer");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("-AppExe");
        startInfo.ArgumentList.Add(exePath);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-RestartAfterInstall");
        startInfo.ArgumentList.Add(restartAfterInstall ? "1" : "0");
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        Process.Start(startInfo);
    }

    private static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        var expected = expectedSha256.Trim().ToLowerInvariant();
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
