using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MolaGPT.Core.Disk;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Installs the Pi half of the sandbox environment: the Node runtime and the Pi
/// harness the Work/BYOK agent runs on.
///
/// Deliberately a sibling of <see cref="PythonRuntimeManager"/> rather than part of
/// it. The two components version independently — Pi ships breaking changes often
/// enough that it is pinned, while the Python runtime moves rarely — so a combined
/// archive would make a Pi patch cost every user a ~200 MB re-download, including
/// the Python half they already have. One concept in the UI, two payloads
/// underneath.
///
/// Node is bundled rather than required: Pi needs Node ≥ 20, almost no Windows
/// machine has it, and bundling also removes "works differently on Node 18" as a
/// class of bug report.
/// </summary>
public sealed class PiSidecarRuntimeManager
{
    public const string DefaultManifestUrl =
        "https://chatgpt.wljay.cn/v2/pi-sidecar-win-x64.json";
    public const int RequiredContractVersion = 10;

    private const string RuntimeDirectoryName = "runtimes";
    private const string StampFileName = ".molagpt-pi-sidecar.json";
    private const string Label = "Agent 运行环境";

    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _baseDirectory;
    private int _installing;
    private readonly SemaphoreSlim _storageOperation = new(1, 1);

    public bool TryBeginStorageCleanup() => _storageOperation.Wait(0);
    public void EndStorageCleanup() => _storageOperation.Release();

    public PiSidecarRuntimeManager(
        HttpClient http,
        string? manifestUrl = null,
        string? baseDirectory = null)
    {
        _http = http;
        _manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl!;
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop",
            "PiSidecar");
    }

    public string BaseDirectory => _baseDirectory;

    public string RuntimeRootDirectory => Path.Combine(BaseDirectory, RuntimeDirectoryName);

    /// <summary>The installed component, or null when it has never been installed
    /// (or the install is incomplete — a half-extracted directory reads as absent
    /// rather than as something to launch).</summary>
    public InstalledPiSidecar? GetInstalled() => FindInstalled(contractVersion: null);

    public InstalledPiSidecar? GetCompatibleInstalled() =>
        FindInstalled(RequiredContractVersion);

    private InstalledPiSidecar? FindInstalled(int? contractVersion)
    {
        if (!Directory.Exists(RuntimeRootDirectory)) return null;

        InstalledPiSidecar? newest = null;
        foreach (var dir in Directory.EnumerateDirectories(RuntimeRootDirectory))
        {
            var stampPath = Path.Combine(dir, StampFileName);
            if (!File.Exists(stampPath)) continue;

            PiSidecarStamp? stamp;
            try { stamp = JsonSerializer.Deserialize<PiSidecarStamp>(File.ReadAllText(stampPath)); }
            catch { continue; }
            if (stamp?.Version is null) continue;

            var node = Path.Combine(dir, stamp.NodeExecutable ?? "node.exe");
            var cli = Path.Combine(dir, stamp.CliJs ?? "");
            var extension = Path.Combine(dir, stamp.Extension ?? "");
            if (!File.Exists(node) || !File.Exists(cli) || !File.Exists(extension)) continue;

            var installedContract = stamp.ContractVersion ?? 0;
            if (contractVersion is { } minimumContract && installedContract < minimumContract) continue;

            var candidate = new InstalledPiSidecar(
                stamp.Version, dir, node, cli, extension, installedContract);
            var comparison = newest is null ? 1
                : Version.TryParse(candidate.Version, out var candidateVersion)
                  && Version.TryParse(newest.Version, out var newestVersion)
                    ? candidateVersion.CompareTo(newestVersion)
                    : string.CompareOrdinal(candidate.Version, newest.Version);
            if (newest is null
                || comparison > 0
                || (comparison == 0
                    && candidate.ContractVersion > newest.ContractVersion))
                newest = candidate;
        }
        return newest;
    }

    public async Task<PiSidecarManifest> FetchManifestAsync(CancellationToken ct = default)
    {
        using var response = await _http
            .GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Deserialised from the stream, not a string: the published manifests carry
        // a UTF-8 BOM (PowerShell writes one), which the stream reader skips and the
        // string overload would choke on.
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var manifest = await JsonSerializer
            .DeserializeAsync<PiSidecarManifest>(stream, cancellationToken: ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{Label}清单为空。");
        manifest.Validate();
        if (manifest.ContractVersion < RequiredContractVersion)
            throw new InvalidOperationException("Agent 运行环境需要更新。");
        return manifest;
    }

    /// <summary>True when an update is published. Never throws: a manifest that
    /// cannot be reached must not stop an already-installed sandbox from running.</summary>
    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var installed = GetCompatibleInstalled();
            if (installed is null) return true;
            var manifest = await FetchManifestAsync(ct).ConfigureAwait(false);
            return !string.Equals(installed.Version, manifest.Version, StringComparison.Ordinal)
                   || installed.ContractVersion < manifest.ContractVersion;
        }
        catch
        {
            return false;
        }
    }

    public async Task<InstalledPiSidecar> DownloadAndInstallAsync(
        IProgress<SandboxProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _storageOperation.WaitAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _installing, 1);
        try { return await DownloadAndInstallCoreAsync(progress, ct).ConfigureAwait(false); }
        finally { Volatile.Write(ref _installing, 0); _storageOperation.Release(); }
    }

    private async Task<InstalledPiSidecar> DownloadAndInstallCoreAsync(
        IProgress<SandboxProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new SandboxProgress("manifest", 0, $"正在检查 {Label}…"));
        var manifest = await FetchManifestAsync(ct).ConfigureAwait(false);

        var downloadDir = Path.Combine(BaseDirectory, "archives");
        var archivePath = Path.Combine(downloadDir, SafeFileName(manifest.FileName!));

        var installed = GetCompatibleInstalled();
        if (installed is not null
            && string.Equals(installed.Version, manifest.Version, StringComparison.Ordinal)
            && installed.ContractVersion >= manifest.ContractVersion)
        {
            // Also on the no-op path, so a machine that updated before this reclaim
            // existed still gets its stranded copy back rather than keeping it until
            // the next version bump.
            Reclaim(keepRuntimeDirectory: installed.Directory, keepArchive: archivePath);
            progress?.Report(new SandboxProgress("done", 1, $"已是最新版本 {installed.Version}"));
            return installed;
        }

        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(downloadDir);

        if (!await SandboxArchive.VerifySha256Async(archivePath, manifest.Sha256!, ct).ConfigureAwait(false))
        {
            var temp = archivePath + ".part";
            await DownloadAsync(manifest, temp, progress, ct).ConfigureAwait(false);

            progress?.Report(new SandboxProgress("verify", 0.82, "正在校验下载文件…"));
            if (!await SandboxArchive.VerifySha256Async(temp, manifest.Sha256!, ct).ConfigureAwait(false))
            {
                TryDelete(temp);
                throw new InvalidOperationException($"{Label}下载文件校验失败，请重试。");
            }
            File.Move(temp, archivePath, overwrite: true);
        }

        progress?.Report(new SandboxProgress("extract", 0.88, "正在解压…"));
        ct.ThrowIfCancellationRequested();
        var target = Path.Combine(
            RuntimeRootDirectory,
            SafeFileName($"{manifest.Version}-contract-{manifest.ContractVersion}"));
        var staging = target + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        try
        {
            SandboxArchive.ExtractSafely(archivePath, staging, Label);

            // Verify before publishing: a partially extracted directory that looked
            // installed would fail later, at a point where the cause is not obvious.
            foreach (var relative in new[] { manifest.NodeExecutable!, manifest.CliJs!, manifest.Extension! })
            {
                if (!File.Exists(Path.Combine(staging, NormalizeRelative(relative))))
                    throw new InvalidOperationException($"{Label}压缩包中找不到 {relative}。");
            }

            File.WriteAllText(
                Path.Combine(staging, StampFileName),
                JsonSerializer.Serialize(new PiSidecarStamp(
                    manifest.Version, manifest.ContractVersion, manifest.NodeExecutable, manifest.CliJs, manifest.Extension,
                    DateTimeOffset.UtcNow)));

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }

        // Only once the new copy is in place, and never before: a reclaim that ran
        // first would leave the machine with nothing if the extract then failed.
        // Superseded copies are ~180MB of runtime plus a ~56MB archive each, and
        // nothing else ever revisits them — raising the contract version alone
        // strands one on every existing install.
        Reclaim(keepRuntimeDirectory: target, keepArchive: archivePath);

        progress?.Report(new SandboxProgress("done", 1, $"{Label} {manifest.Version} 已配置完成"));
        return GetCompatibleInstalled() ?? throw new InvalidOperationException($"{Label}安装后仍不可用。");
    }

    /// <summary>Delete every installed runtime and cached archive except the pair
    /// just installed. Best effort throughout: a directory still held by a running
    /// sidecar simply survives to the next install.</summary>
    private void Reclaim(string keepRuntimeDirectory, string keepArchive)
    {
        foreach (var path in ListSupersededPathsCore(keepRuntimeDirectory, keepArchive))
        {
            if (Directory.Exists(path)) TryDelete(Path.Combine(path, StampFileName));
            DiskFootprint.Delete(path);
        }
    }

    /// <summary>
    /// Runtime directories and archives that do not belong to the installed
    /// version. The install path already reclaims these, but only when it runs and
    /// only what is not held open at that moment — a sidecar still on the old
    /// runtime keeps its directory until something asks again. This is that ask,
    /// for callers without a manifest: the keeper is whatever is installed, and an
    /// archive belongs to it when its name carries the same version and contract.
    /// Empty when nothing is installed.
    /// </summary>
    public IReadOnlyList<string> ListSupersededPaths() => ListSupersededPathsCore(null, null);

    private IReadOnlyList<string> ListSupersededPathsCore(string? keepDirectory, string? keepArchive)
    {
        if (keepDirectory is null && Volatile.Read(ref _installing) != 0) return [];
        if (IsReparsePoint(BaseDirectory) || IsReparsePoint(Path.GetDirectoryName(BaseDirectory))
            || IsReparsePoint(RuntimeRootDirectory)) return [];
        var keep = keepDirectory ?? (GetCompatibleInstalled() ?? GetInstalled())?.Directory;
        if (keep is null) return [];
        var keepName = Path.GetFileName(Path.TrimEndingDirectorySeparator(keep));

        var paths = new List<string>();
        try
        {
            foreach (var directory in new DirectoryInfo(RuntimeRootDirectory).EnumerateDirectories())
            {
                if (PathEquals(directory.FullName, keep)) continue;
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !System.Text.RegularExpressions.Regex.IsMatch(directory.Name,
                        @"^\d+(?:\.\d+){1,3}-contract-\d+(?:\.staging)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;
                // An install in flight extracts into a staging directory.
                if (directory.Name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)
                    && directory.CreationTimeUtc > DateTime.UtcNow.AddHours(-1))
                    continue;
                if (!directory.Name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(Path.Combine(directory.FullName, StampFileName)))
                {
                    var entries = directory.GetFileSystemInfos();
                    if (entries.Length != 1 || entries[0] is not FileInfo
                        || !entries[0].Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                }
                var options = new EnumerationOptions { RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                var running = false;
                foreach (var node in Directory.EnumerateFiles(directory.FullName, "node.exe", options))
                {
                    try { using var probe = new FileStream(node, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { running = true; break; }
                    catch (UnauthorizedAccessException) { running = true; break; }
                }
                if (running) continue;
                paths.Add(directory.FullName);
            }

            foreach (var file in IsReparsePoint(Path.Combine(BaseDirectory, "archives")) ? [] : EnumerateArchives())
            {
                if ((keepArchive is null || !PathEquals(file, keepArchive))
                    && !Path.GetFileName(file).Contains(keepName, StringComparison.OrdinalIgnoreCase))
                    paths.Add(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }

    /// <summary>The installed version's downloaded archive. Only a cache.</summary>
    public IReadOnlyList<string> ListArchiveCachePaths()
    {
        if (Volatile.Read(ref _installing) != 0 || IsReparsePoint(BaseDirectory)
            || IsReparsePoint(Path.GetDirectoryName(BaseDirectory))
            || IsReparsePoint(Path.Combine(BaseDirectory, "archives"))) return [];
        var superseded = ListSupersededPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { return EnumerateArchives().Where(file => !superseded.Contains(file)).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool IsReparsePoint(string? path) => path is not null && Directory.Exists(path)
        && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private IEnumerable<string> EnumerateArchives()
    {
        var archives = Path.Combine(BaseDirectory, "archives");
        if (!Directory.Exists(archives)) return [];
        return Directory.EnumerateFiles(archives)
            .Where(file => Path.GetFileName(file).StartsWith("MolaGPT.PiSidecar-", StringComparison.OrdinalIgnoreCase)
                           && (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                               || file.EndsWith(".zip.part", StringComparison.OrdinalIgnoreCase)))
            // A download in progress writes a .part; an abandoned one stops being touched.
            .Where(file => !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                           || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Remove every installed version and the download cache.</summary>
    public void Delete()
    {
        _storageOperation.Wait();
        try
        {
            if (Directory.Exists(RuntimeRootDirectory))
            {
                foreach (var dir in Directory.EnumerateDirectories(RuntimeRootDirectory))
                    TryDelete(Path.Combine(dir, StampFileName));
            }

            foreach (var dir in new[] { RuntimeRootDirectory, Path.Combine(BaseDirectory, "archives") })
            {
                if (!Directory.Exists(dir)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* in use — next time */ }
            }
        }
        finally { _storageOperation.Release(); }
    }

    private async Task DownloadAsync(
        PiSidecarManifest manifest, string destination, IProgress<SandboxProgress>? progress, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? manifest.SizeBytes ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0)
            {
                var fraction = 0.05 + 0.75 * ((double)written / total);
                progress?.Report(new SandboxProgress(
                    "download", fraction, $"正在下载 {written / 1048576} / {total / 1048576} MB"));
            }
        }
    }

    private static string NormalizeRelative(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string SafeFileName(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars()));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private sealed record PiSidecarStamp(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("contract_version")] int? ContractVersion,
        [property: JsonPropertyName("node_executable")] string? NodeExecutable,
        [property: JsonPropertyName("cli_js")] string? CliJs,
        [property: JsonPropertyName("extension")] string? Extension,
        [property: JsonPropertyName("installed_at")] DateTimeOffset InstalledAt);
}

public sealed record InstalledPiSidecar(
    string Version,
    string Directory,
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    int ContractVersion = 0);

/// <summary>Install progress for one sandbox component.</summary>
public sealed record SandboxProgress(string Stage, double Fraction, string Message);

public sealed record PiSidecarManifest(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("contract_version")] int ContractVersion,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("node_executable")] string? NodeExecutable,
    [property: JsonPropertyName("cli_js")] string? CliJs,
    [property: JsonPropertyName("extension")] string? Extension)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version)) throw new InvalidOperationException("清单缺少 version。");
        if (ContractVersion < 1) throw new InvalidOperationException("清单缺少 contract_version。");
        if (string.IsNullOrWhiteSpace(Url)) throw new InvalidOperationException("清单缺少 url。");
        if (string.IsNullOrWhiteSpace(Sha256)) throw new InvalidOperationException("清单缺少 sha256。");
        if (string.IsNullOrWhiteSpace(FileName)) throw new InvalidOperationException("清单缺少 file_name。");
        if (string.IsNullOrWhiteSpace(NodeExecutable)) throw new InvalidOperationException("清单缺少 node_executable。");
        if (string.IsNullOrWhiteSpace(CliJs)) throw new InvalidOperationException("清单缺少 cli_js。");
        if (string.IsNullOrWhiteSpace(Extension)) throw new InvalidOperationException("清单缺少 extension。");

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("清单 url 必须是 https。");
    }
}
