namespace MolaGPT.Core.Disk;

/// <summary>What a delete actually achieved. Files held open by another process
/// are counted, not thrown: a cleanup that stops at the first locked file frees
/// nothing behind it.</summary>
public readonly record struct DiskDeleteResult(long BytesFreed, int FilesSkipped)
{
    public static DiskDeleteResult operator +(DiskDeleteResult left, DiskDeleteResult right) =>
        new(left.BytesFreed + right.BytesFreed, left.FilesSkipped + right.FilesSkipped);
}

/// <summary>
/// Measures and deletes files and directory trees without ever crossing a
/// reparse point.
///
/// That one rule is why this exists instead of <c>Directory.Delete(path, true)</c>
/// and <c>EnumerateFiles(…, AllDirectories)</c>: Python workspaces hold whatever a
/// script created, including junctions, and a walk that follows one measures —
/// or deletes — a tree that was never ours. A link is removed as a link; its
/// target is not touched.
/// </summary>
public static class DiskFootprint
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Skipped entries are not recursed into either, which is the point.
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <summary>Bytes held by a file or a directory tree. Zero when missing.</summary>
    public static long Measure(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
                return file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? 0 : file.Length;

            var directory = new DirectoryInfo(path);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;

            long total = 0;
            // FileInfo from the walk already carries its length; a second stat per
            // file doubles the cost on the ~15k-file runtime directories.
            foreach (var entry in directory.EnumerateFiles("*", Walk))
            {
                try { total += entry.Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public static long Measure(IEnumerable<string> paths) => paths.Sum(Measure);

    /// <summary>Delete a file or a directory tree, best effort.</summary>
    public static DiskDeleteResult Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return default;
        try
        {
            if (File.Exists(path)) return DeleteFile(new FileInfo(path));
            return Directory.Exists(path) ? DeleteDirectory(new DirectoryInfo(path)) : default;
        }
        catch (IOException) { return new DiskDeleteResult(0, 1); }
        catch (UnauthorizedAccessException) { return new DiskDeleteResult(0, 1); }
    }

    public static DiskDeleteResult Delete(IEnumerable<string> paths)
    {
        var result = default(DiskDeleteResult);
        foreach (var path in paths) result += Delete(path);
        return result;
    }

    private static DiskDeleteResult DeleteDirectory(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Non-recursive on purpose: this unlinks the junction and leaves
            // whatever it points at alone.
            try { directory.Delete(false); return default; }
            catch (IOException) { return new DiskDeleteResult(0, 1); }
            catch (UnauthorizedAccessException) { return new DiskDeleteResult(0, 1); }
        }

        var result = default(DiskDeleteResult);
        FileSystemInfo[] entries;
        try { entries = directory.GetFileSystemInfos(); }
        catch (IOException) { return new DiskDeleteResult(0, 1); }
        catch (UnauthorizedAccessException) { return new DiskDeleteResult(0, 1); }

        foreach (var entry in entries)
        {
            result += entry is DirectoryInfo child
                ? DeleteDirectory(child)
                : DeleteFile((FileInfo)entry);
        }

        // Fails when anything was skipped; the survivors keep their directory.
        try { directory.Delete(false); }
        catch (IOException) { if (result.FilesSkipped == 0) result += new DiskDeleteResult(0, 1); }
        catch (UnauthorizedAccessException) { if (result.FilesSkipped == 0) result += new DiskDeleteResult(0, 1); }
        return result;
    }

    private static DiskDeleteResult DeleteFile(FileInfo file)
    {
        try
        {
            var isLink = file.Attributes.HasFlag(FileAttributes.ReparsePoint);
            var length = isLink ? 0 : file.Length;
            if (!isLink && file.Attributes.HasFlag(FileAttributes.ReadOnly))
                file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
            return new DiskDeleteResult(length, 0);
        }
        catch (IOException) { return new DiskDeleteResult(0, 1); }
        catch (UnauthorizedAccessException) { return new DiskDeleteResult(0, 1); }
    }
}
