using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MolaGPT.Desktop.Services;

public sealed class PythonRuntimeManager
{
    public const string DefaultManifestUrl =
        "https://chatgpt.wljay.cn/v2/python-runtime-win-x64.json";

    private const string RuntimeDirectoryName = "runtimes";
    private const string StampFileName = ".molagpt-python-runtime.json";
    private const string ActiveRuntimeFileName = ".active-runtime.json";
    private readonly HttpClient _http;
    private readonly string _manifestUrl;

    public PythonRuntimeManager(HttpClient http, string? manifestUrl = null)
    {
        _http = http;
        _manifestUrl = string.IsNullOrWhiteSpace(manifestUrl)
            ? DefaultManifestUrl
            : manifestUrl!;
    }

    public string RuntimeBaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT Desktop",
        "PythonRuntime");

    public string RuntimeRootDirectory => Path.Combine(RuntimeBaseDirectory, RuntimeDirectoryName);

    public string RuntimeDirectory => GetInstalledRuntime()?.RuntimeDirectory ?? RuntimeRootDirectory;

    public bool IsManagedInterpreterPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return IsPathInside(path, RuntimeRootDirectory)
                   || IsPathInside(path, GetLegacyRuntimeDirectory());
        }
        catch
        {
            return false;
        }
    }

    public void DeleteRuntime()
    {
        DeleteManagedDirectory(RuntimeRootDirectory);
        DeleteManagedDirectory(Path.Combine(RuntimeBaseDirectory, "archives"));

        // Clean up the legacy app-local one-click runtime as part of migration.
        // A manually selected interpreter is never inside this exact directory.
        var legacy = GetLegacyRuntimeDirectory();
        if (Directory.Exists(legacy))
            DeleteManagedDirectory(legacy, GetAppDirectory());

        ResetSessionDependencyOverlays();
    }

    public InstalledPythonRuntime? GetInstalledRuntime()
    {
        try
        {
            var activePath = Path.Combine(RuntimeRootDirectory, ActiveRuntimeFileName);
            if (File.Exists(activePath))
            {
                var active = JsonSerializer.Deserialize<ActiveRuntimeStamp>(File.ReadAllText(activePath));
                if (!string.IsNullOrWhiteSpace(active?.DirectoryName))
                {
                    var activeDir = Path.Combine(RuntimeRootDirectory, SafeDirectoryName(active.DirectoryName));
                    if (TryReadInstalledRuntime(activeDir) is { } selected)
                        return selected;
                }
            }

            if (Directory.Exists(RuntimeRootDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(RuntimeRootDirectory)
                             .OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    if (TryReadInstalledRuntime(directory) is { } installed)
                        return installed;
                }
            }

            // Read old installations so the UI can migrate them on the next
            // one-click configuration instead of silently losing the setting.
            return TryReadInstalledRuntime(GetLegacyRuntimeDirectory(), allowMissingStamp: true);
        }
        catch
        {
            return null;
        }
    }

    public PythonRuntimeStorageUsage GetStorageUsage()
    {
        var runtimeBytes = GetDirectorySize(RuntimeBaseDirectory) + GetDirectorySize(GetLegacyRuntimeDirectory());
        var sessionBytes = GetSessionDependencyOverlaySize();
        return new PythonRuntimeStorageUsage(runtimeBytes, sessionBytes);
    }

    public async Task<PythonRuntimeManifest> FetchManifestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
        request.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<PythonRuntimeManifest>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        if (manifest is null)
            throw new InvalidOperationException("Python 运行时清单为空。");
        manifest.Validate();
        return manifest;
    }

    public async Task<InstalledPythonRuntime> DownloadAndInstallAsync(
        IProgress<PythonRuntimeProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new PythonRuntimeProgress("manifest", 0, "正在获取 Python 运行时清单..."));
        var manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
        var version = manifest.Version!;
        var runtime = manifest.Runtime!;
        var sha256 = manifest.Sha256!;
        var installed = GetInstalledRuntime();
        if (installed is not null
            && IsPathInside(installed.RuntimeDirectory, RuntimeRootDirectory)
            && IsInstalledRuntimeCurrent(installed, manifest))
        {
            progress?.Report(new PythonRuntimeProgress("done", 1, $"已是最新版本 {installed.Version}"));
            return installed;
        }

        EnsureWritableRuntimeRoot();

        var downloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop",
            "PythonRuntime",
            "archives",
            version);
        Directory.CreateDirectory(downloadDir);
        var archivePath = Path.Combine(downloadDir, SafeFileName(manifest.FileName ?? $"python-runtime-{version}.zip"));
        var tempArchivePath = archivePath + ".download";

        if (!File.Exists(archivePath) || !await VerifySha256Async(archivePath, sha256, ct).ConfigureAwait(false))
        {
            await DownloadAsync(manifest, tempArchivePath, progress, ct).ConfigureAwait(false);
            progress?.Report(new PythonRuntimeProgress("verify", 0.82, "正在校验下载文件..."));
            if (!await VerifySha256Async(tempArchivePath, sha256, ct).ConfigureAwait(false))
            {
                File.Delete(tempArchivePath);
                throw new InvalidOperationException("Python 运行时校验失败，请稍后重试。");
            }
            if (File.Exists(archivePath)) File.Delete(archivePath);
            File.Move(tempArchivePath, archivePath);
        }

        progress?.Report(new PythonRuntimeProgress("extract", 0.88, "正在解压到 MolaGPT 目录..."));
        var stagingDir = Path.Combine(RuntimeRootDirectory, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            ExtractZipSafely(archivePath, stagingDir);
            NormalizeSingleRootDirectory(stagingDir);

            var pythonRelative = NormalizeRelativePath(manifest.PythonExecutable);
            var pythonPath = Path.Combine(stagingDir, pythonRelative);
            if (!File.Exists(pythonPath))
                throw new InvalidOperationException($"运行时压缩包中找不到 {manifest.PythonExecutable}。");

            var stamp = new RuntimeStamp(
                version,
                runtime,
                pythonRelative,
                manifest.Packages ?? Array.Empty<string>(),
                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, StampFileName),
                JsonSerializer.Serialize(stamp, RuntimeJsonOptions),
                ct).ConfigureAwait(false);

            var directoryName = SafeDirectoryName(version);
            var finalRuntimeDirectory = Path.Combine(RuntimeRootDirectory, directoryName);
            ReplaceRuntimeDirectory(stagingDir, finalRuntimeDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(RuntimeRootDirectory, ActiveRuntimeFileName),
                JsonSerializer.Serialize(new ActiveRuntimeStamp(directoryName), RuntimeJsonOptions),
                ct).ConfigureAwait(false);
            var finalPythonPath = Path.Combine(finalRuntimeDirectory, pythonRelative);
            progress?.Report(new PythonRuntimeProgress("done", 1, $"Python 运行时 {version} 已配置完成"));
            return new InstalledPythonRuntime(
                version,
                runtime,
                finalPythonPath,
                finalRuntimeDirectory,
                manifest.Packages ?? Array.Empty<string>());
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    private async Task DownloadAsync(
        PythonRuntimeManifest manifest,
        string tempArchivePath,
        IProgress<PythonRuntimeProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url!);
        request.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? manifest.SizeBytes;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(tempArchivePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;

            var ratio = total is > 0 ? Math.Clamp((double)readTotal / total.Value, 0, 1) : 0;
            progress?.Report(new PythonRuntimeProgress(
                "download",
                0.05 + ratio * 0.75,
                total is > 0
                    ? $"正在下载 Python 运行时 {FormatBytes(readTotal)} / {FormatBytes(total.Value)}"
                    : $"正在下载 Python 运行时 {FormatBytes(readTotal)}"));
        }
    }

    private static bool IsInstalledRuntimeCurrent(
        InstalledPythonRuntime installed,
        PythonRuntimeManifest manifest)
    {
        if (!string.Equals(installed.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(installed.Runtime, manifest.Runtime, StringComparison.OrdinalIgnoreCase))
            return false;

        var manifestPackages = NormalizePackageSet(manifest.Packages);
        if (manifestPackages.Count == 0)
            return true;

        var installedPackages = NormalizePackageSet(installed.Packages);
        return installedPackages.SetEquals(manifestPackages);
    }

    private static HashSet<string> NormalizePackageSet(IReadOnlyList<string>? packages) =>
        (packages ?? Array.Empty<string>())
        .Where(package => !string.IsNullOrWhiteSpace(package))
        .Select(package => package.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ExtractZipSafely(string archivePath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destinationDir);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!destinationPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destinationPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Python 运行时压缩包包含非法路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void NormalizeSingleRootDirectory(string stagingDir)
    {
        if (File.Exists(Path.Combine(stagingDir, "python.exe")))
            return;

        var children = Directory.GetFileSystemEntries(stagingDir);
        if (children.Length != 1 || !Directory.Exists(children[0]))
            return;

        var root = children[0];
        if (!File.Exists(Path.Combine(root, "python.exe")))
            return;

        foreach (var child in Directory.GetFileSystemEntries(root))
        {
            var target = Path.Combine(stagingDir, Path.GetFileName(child));
            if (Directory.Exists(child))
                Directory.Move(child, target);
            else
                File.Move(child, target);
        }
        Directory.Delete(root, recursive: true);
    }

    private static void ReplaceRuntimeDirectory(string stagingDir, string runtimeDir)
    {
        var backupDir = runtimeDir + ".old-" + Guid.NewGuid().ToString("N");
        var hadExisting = Directory.Exists(runtimeDir);
        if (hadExisting)
            Directory.Move(runtimeDir, backupDir);

        try
        {
            Directory.Move(stagingDir, runtimeDir);
        }
        catch
        {
            if (hadExisting && Directory.Exists(backupDir) && !Directory.Exists(runtimeDir))
                Directory.Move(backupDir, runtimeDir);
            throw;
        }
        TryDeleteDirectory(backupDir);
    }

    private static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path))
            return false;

        var expected = expectedSha256.Trim().ToLowerInvariant();
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureWritableRuntimeRoot()
    {
        Directory.CreateDirectory(RuntimeRootDirectory);
        var probe = Path.Combine(RuntimeRootDirectory, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"MolaGPT 运行时目录不可写：{RuntimeRootDirectory}", ex);
        }
    }

    private InstalledPythonRuntime? TryReadInstalledRuntime(string runtimeDirectory, bool allowMissingStamp = false)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
            return null;

        var stampPath = Path.Combine(runtimeDirectory, StampFileName);
        if (!File.Exists(stampPath))
        {
            if (!allowMissingStamp) return null;
            var fallbackPython = Path.Combine(runtimeDirectory, "python.exe");
            return File.Exists(fallbackPython)
                ? new InstalledPythonRuntime("unknown", "legacy", fallbackPython, runtimeDirectory, Array.Empty<string>())
                : null;
        }

        var stamp = JsonSerializer.Deserialize<RuntimeStamp>(File.ReadAllText(stampPath));
        if (stamp is null) return null;
        var pythonPath = Path.Combine(runtimeDirectory, NormalizeRelativePath(stamp.PythonExecutable));
        return File.Exists(pythonPath)
            ? new InstalledPythonRuntime(
                stamp.Version,
                stamp.Runtime,
                pythonPath,
                runtimeDirectory,
                stamp.Packages ?? Array.Empty<string>())
            : null;
    }

    private static string GetLegacyRuntimeDirectory() =>
        Path.Combine(GetAppDirectory(), "python");

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteManagedDirectory(string path, string? requiredRoot = null)
    {
        if (!Directory.Exists(path)) return;
        var root = requiredRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop",
            "PythonRuntime");
        if (!IsPathInside(path, root))
            throw new InvalidOperationException($"拒绝删除托管目录之外的 Python 环境：{path}");
        Directory.Delete(path, recursive: true);
    }

    private static string GetSessionRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT",
        "python-tool",
        "sessions");

    private static readonly string[] SessionEnvironmentDirectories =
    [
        ".packages", ".pip-cache", ".uv-cache", ".tmp", ".appdata", ".localappdata", ".matplotlib", "__pycache__"
    ];

    private static void ResetSessionDependencyOverlays()
    {
        var root = GetSessionRootDirectory();
        if (!Directory.Exists(root)) return;
        foreach (var session in Directory.EnumerateDirectories(root))
        {
            foreach (var name in SessionEnvironmentDirectories)
                TryDeleteDirectory(Path.Combine(session, name));
        }
    }

    private static long GetSessionDependencyOverlaySize()
    {
        var root = GetSessionRootDirectory();
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var session in Directory.EnumerateDirectories(root))
        {
            foreach (var name in SessionEnvironmentDirectories)
                total += GetDirectorySize(Path.Combine(session, name));
        }
        return total;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    private static string GetAppDirectory()
    {
        var processPath = Environment.ProcessPath;
        var dir = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
        return string.IsNullOrWhiteSpace(dir)
            ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : dir!;
    }

    private static string NormalizeRelativePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "python.exe" : path!.Trim();
        value = value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Python 运行时清单包含非法 python_executable 路径。");
        return value;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars);
    }

    private static string SafeDirectoryName(string value)
    {
        var safe = SafeFileName(value).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(safe))
            throw new InvalidOperationException("Python 运行时版本不能映射为安全目录名。");
        return safe;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup; a locked old runtime can be removed on a later update.
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / 1024d / 1024d:0.##} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }

    private static readonly JsonSerializerOptions RuntimeJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RuntimeStamp(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("runtime")] string Runtime,
        [property: JsonPropertyName("python_executable")] string PythonExecutable,
        [property: JsonPropertyName("packages")] IReadOnlyList<string> Packages,
        [property: JsonPropertyName("installed_at")] DateTimeOffset InstalledAt);

    private sealed record ActiveRuntimeStamp(
        [property: JsonPropertyName("directory_name")] string DirectoryName);
}

public sealed record InstalledPythonRuntime(
    string Version,
    string Runtime,
    string PythonExecutablePath,
    string RuntimeDirectory,
    IReadOnlyList<string> Packages);

public sealed record PythonRuntimeProgress(string Stage, double Progress, string Message);

public sealed record PythonRuntimeStorageUsage(long RuntimeBytes, long SessionEnvironmentBytes)
{
    public long TotalBytes => RuntimeBytes + SessionEnvironmentBytes;
}

public sealed record PythonRuntimeManifest(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("python_executable")] string? PythonExecutable,
    [property: JsonPropertyName("packages")] IReadOnlyList<string>? Packages)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
            throw new InvalidOperationException("Python 运行时清单缺少 version。");
        if (string.IsNullOrWhiteSpace(Runtime))
            throw new InvalidOperationException("Python 运行时清单缺少 runtime。");
        if (string.IsNullOrWhiteSpace(Url) || !Uri.TryCreate(Url, UriKind.Absolute, out _))
            throw new InvalidOperationException("Python 运行时清单缺少有效下载地址。");
        if (string.IsNullOrWhiteSpace(Sha256) || Sha256.Trim().Length != 64)
            throw new InvalidOperationException("Python 运行时清单缺少有效 SHA256。");
    }
}
