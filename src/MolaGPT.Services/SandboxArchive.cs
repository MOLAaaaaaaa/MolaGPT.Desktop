using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// The security-sensitive half of installing a sandbox component: verifying what
/// was downloaded and unpacking it without letting the archive write outside the
/// destination.
///
/// Shared by every component of the sandbox environment (the Python runtime, the
/// Pi sidecar) rather than copied per component — a second copy of a path-traversal
/// guard is a second chance to get it subtly wrong, and only one of them would get
/// fixed.
/// </summary>
internal static class SandboxArchive
{
    /// <summary>True when the file on disk matches the manifest's digest. A missing
    /// file or a blank expectation is a mismatch, never a pass.</summary>
    public static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path))
            return false;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash),
            expectedSha256.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract every entry, refusing any whose resolved path escapes
    /// <paramref name="destinationDir"/> — the zip-slip guard. <paramref name="label"/>
    /// only names the component in the error.
    /// </summary>
    public static void ExtractSafely(string archivePath, string destinationDir, string label)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destinationDir);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!destinationPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destinationPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{label}压缩包包含非法路径。");
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
}
