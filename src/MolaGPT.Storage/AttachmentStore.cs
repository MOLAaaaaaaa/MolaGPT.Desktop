using System.Security.Cryptography;

namespace MolaGPT.Storage;

/// <summary>
/// Content-addressed local store for BYOK image attachments. Bytes are written
/// to <c>%LocalAppData%\MolaGPT\attachments\&lt;sha256&gt;.&lt;ext&gt;</c> and
/// referenced from message meta by relative file name only (so the reference
/// survives DB moves). BYOK conversations never sync to the cloud, so keeping
/// the bytes purely local has no cross-device cost.
///
/// Naming is by SHA-256 of the content, which de-duplicates identical images
/// across messages/conversations for free: re-saving the same bytes is a no-op.
/// </summary>
public sealed class AttachmentStore
{
    private readonly string _root;

    public AttachmentStore(string? root = null)
    {
        _root = root ?? DefaultRoot();
        Directory.CreateDirectory(_root);
    }

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "attachments");

    /// <summary>Persist bytes and return the relative file name (e.g.
    /// <c>3fa9...e1.png</c>). Returns null for empty input.</summary>
    public string? Save(byte[] bytes, string? mimeType, string? fileName)
    {
        if (bytes is not { Length: > 0 }) return null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var ext = GuessExtension(mimeType, fileName);
        var relativeName = ext.Length > 0 ? $"{hash}.{ext}" : hash;
        var fullPath = Path.Combine(_root, relativeName);
        // Content-addressed: identical bytes hash to the same name, so an
        // existing file is already correct and we skip the rewrite.
        if (!File.Exists(fullPath))
            File.WriteAllBytes(fullPath, bytes);
        return relativeName;
    }

    public byte[]? Load(string? relativeName)
    {
        if (!TryGetPath(relativeName, out var fullPath)) return null;
        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Resolve a relative name to a full path iff the file exists.
    /// Rejects names that try to escape the store root.</summary>
    public bool TryGetPath(string? relativeName, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativeName)) return false;
        // Reject path separators / traversal — names are bare hashes.
        if (relativeName.Contains('/') || relativeName.Contains('\\')
            || relativeName.Contains("..", StringComparison.Ordinal))
            return false;
        var candidate = Path.Combine(_root, relativeName);
        if (!File.Exists(candidate)) return false;
        fullPath = candidate;
        return true;
    }

    /// <summary>Best-effort delete. Missing/locked files are ignored; callers
    /// pass the relative names harvested from message meta.</summary>
    public void Delete(IEnumerable<string?> relativeNames)
    {
        foreach (var name in relativeNames)
        {
            if (!TryGetPath(name, out var fullPath)) continue;
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GuessExtension(string? mimeType, string? fileName)
    {
        var fromMime = (mimeType ?? string.Empty).ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            _ => string.Empty
        };
        if (fromMime.Length > 0) return fromMime;

        var ext = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return ext.Length is > 0 and <= 5 ? ext : string.Empty;
    }
}
