using System.Diagnostics;
using System.Security.Cryptography;

namespace MolaGPT.App.Infrastructure;

internal sealed class AppAutoUpdateService(HttpClient http)
{
    public sealed record UpdatePackage(string Version, string DownloadUrl, string Sha256, string FileName);

    public async Task<string> DownloadAndVerifyAsync(
        UpdatePackage package,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop", "Updates", package.Version);
        Directory.CreateDirectory(targetDir);

        var fileName = string.IsNullOrWhiteSpace(package.FileName)
            ? $"MolaGPT.Desktop-{package.Version}-win-x64-setup.exe"
            : package.FileName;
        var targetPath = Path.Combine(targetDir, fileName);
        var temporaryPath = targetPath + ".download";

        if (File.Exists(targetPath) && await VerifySha256Async(targetPath, package.Sha256, ct))
        {
            progress?.Report(1);
            return targetPath;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total is > 0) progress?.Report(Math.Clamp((double)received / total.Value, 0, 1));
            }
        }

        if (!await VerifySha256Async(temporaryPath, package.Sha256, ct))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("安装包校验失败，请稍后重试。");
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(temporaryPath, targetPath);
        progress?.Report(1);
        return targetPath;
    }

    public static void StartInstallerAfterExit(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("安装包不存在。", installerPath);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法定位当前程序路径。");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"molagpt-update-{Environment.ProcessId}-{Guid.NewGuid():N}.ps1");
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop", "Updates");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "update-installer.log");
        File.WriteAllText(scriptPath, InstallerScript);

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
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        Process.Start(startInfo);
    }

    private static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private const string InstallerScript = """
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$AppExe,
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = "Stop"

function Write-UpdateLog([string]$Message) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -LiteralPath $LogPath -Value "$stamp $Message" -Encoding UTF8
}

try {
    Write-UpdateLog "waiting for parent pid $ParentPid"
    Wait-Process -Id $ParentPid -ErrorAction SilentlyContinue
    Write-UpdateLog "starting installer: $Installer"
    $process = Start-Process -FilePath $Installer -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Wait -PassThru
    Write-UpdateLog "installer exit code: $($process.ExitCode)"
    if ($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $AppExe)) {
        Start-Process -FilePath $AppExe
    }
} catch {
    Write-UpdateLog "failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    throw
} finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
}
