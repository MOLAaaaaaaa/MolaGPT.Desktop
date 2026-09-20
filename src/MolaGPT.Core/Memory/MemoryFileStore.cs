using System.Text;
using System.Text.Json;

using MemorySection = MolaGPT.Core.Personalization.MemorySection;

namespace MolaGPT.Core.Memory;

/// <summary>
/// The memory files on disk, which are the source of truth. The SQLite index can
/// be deleted and rebuilt; these cannot.
///
/// Everything here is written for a file the user may have open in his own
/// editor at this very moment. Three consequences: writes are surgical line
/// edits rather than whole-file renders, every write re-checks the modification
/// stamp it read and replays itself once if the file moved underneath it, and
/// the previous version is copied into <c>.history/</c> first. The user saving
/// his editor over our write is not an edge case — it will happen, and on a
/// desktop the affordable answer is a snapshot, not a lock.
/// </summary>
public sealed class MemoryFileStore
{
    public const string MemoryFileName = "MEMORY.md";
    public const string ProfileFileName = "profile.md";
    public const string HistoryDirectoryName = ".history";
    private const int HistoryKeep = 20;

    private readonly object _writeLock = new();

    public MemoryFileStore(string? rootOverride = null)
    {
        Root = rootOverride ?? DefaultRoot();
    }

    public string Root { get; }
    public string MemoryPath => Path.Combine(Root, MemoryFileName);
    public string ProfilePath => Path.Combine(Root, ProfileFileName);
    public string TopicsPath => Path.Combine(Root, "topics.json");
    public string HistoryDirectory => Path.Combine(Root, HistoryDirectoryName);

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT",
        "memory");

    public void EnsureDirectory() => Directory.CreateDirectory(Root);

    /// <summary>Modification stamp of MEMORY.md, or <see cref="DateTime.MinValue"/>
    /// when it does not exist. The cheap half of "did anything change".</summary>
    public DateTime MemoryStamp()
    {
        try
        {
            return File.Exists(MemoryPath) ? File.GetLastWriteTimeUtc(MemoryPath) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    public MemoryDocument Read()
    {
        try
        {
            if (!File.Exists(MemoryPath)) return MemoryDocument.Empty;
            var stamp = File.GetLastWriteTimeUtc(MemoryPath);
            var text = ReadAllTextShared(MemoryPath);
            return new MemoryDocument(text, MemoryMarkdown.Parse(text), stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoryDocument.Empty;
        }
    }

    public MemoryProfile ReadProfile()
    {
        try
        {
            return File.Exists(ProfilePath)
                ? MemoryMarkdown.ParseProfile(ReadAllTextShared(ProfilePath))
                : MemoryProfile.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoryProfile.Empty;
        }
    }

    public void WriteProfile(MemoryProfile profile)
    {
        lock (_writeLock)
        {
            EnsureDirectory();
            Snapshot(ProfilePath);
            AtomicWrite(ProfilePath, MemoryMarkdown.RenderProfile(profile));
        }
    }

    public IReadOnlyList<MemoryTopic> ReadTopics() => File.Exists(TopicsPath)
        ? JsonSerializer.Deserialize<List<MemoryTopic>>(ReadAllTextShared(TopicsPath))!
        : [];

    public MemoryTopic SaveTopic(MemoryTopic topic)
    {
        lock (_writeLock)
        {
            var topics = ReadTopics().ToList();
            var saved = topic with { UpdatedAt = DateOnly.FromDateTime(DateTime.Now) };
            topics.RemoveAll(item => item.Id == topic.Id);
            topics.Add(saved);
            EnsureDirectory();
            Snapshot(TopicsPath);
            AtomicWrite(TopicsPath, JsonSerializer.Serialize(topics, new JsonSerializerOptions { WriteIndented = true }));
            return saved;
        }
    }

    public void RemoveTopic(string id)
    {
        lock (_writeLock)
        {
            var topics = ReadTopics().Where(topic => topic.Id != id).ToArray();
            EnsureDirectory();
            Snapshot(TopicsPath);
            AtomicWrite(TopicsPath, JsonSerializer.Serialize(topics));
        }
    }

    public MemoryWriteResult AssignTopic(MemoryEntry entry, string topicId) => Mutate(document =>
    {
        var current = Relocate(document, entry);
        if (current is null) return MemoryMutation.Fail("记忆已不存在。");
        var updated = MemoryMarkdown.ReplaceLine(document.RawText, current.LineIndex, current.Text,
            MemoryMarkdown.RenderEntry(current with { TopicId = topicId }));
        return updated is null ? MemoryMutation.Fail("记忆已被修改，请重新读取后重试。") : MemoryMutation.Write(updated, current.Id);
    });

    // ---- writes ------------------------------------------------------------

    public MemoryWriteResult Add(MemorySection section, string text, MemoryOrigin origin,
        double confidence = 1, string? profileKey = null, DateOnly? today = null, string? topicId = null)
    {
        var clean = text.Trim();
        if (clean.Length == 0) return MemoryWriteResult.Fail("记忆内容不能为空。");

        return Mutate(document =>
        {
            var duplicate = document.Entries.FirstOrDefault(entry =>
                string.Equals(MemoryGuards.NormalizeKey(entry.Text), MemoryGuards.NormalizeKey(clean),
                    StringComparison.Ordinal));
            if (duplicate is not null)
                return MemoryMutation.NoChange($"已有相同记忆：{duplicate.Text}", duplicate.Id);

            var entry = new MemoryEntry
            {
                Id = MemoryMarkdown.NewId(),
                Section = section,
                Text = clean,
                Confidence = Math.Clamp(confidence, 0, 1),
                Origin = origin,
                LastReinforced = today ?? DateOnly.FromDateTime(DateTime.Now),
                ProfileKey = MemoryProfile.IsKnownKey(profileKey) ? profileKey : null,
                TopicId = topicId
            };
            return MemoryMutation.Write(MemoryMarkdown.InsertEntry(document.RawText, entry), entry.Id);
        });
    }

    public MemoryWriteResult Replace(MemoryEntry target, string text, MemoryOrigin origin, DateOnly? today = null)
    {
        var clean = text.Trim();
        if (clean.Length == 0) return MemoryWriteResult.Fail("记忆内容不能为空。");

        return Mutate(document =>
        {
            var current = Relocate(document, target);
            if (current is null) return MemoryMutation.Fail("记忆已不存在。");

            var replacement = MemoryMarkdown.RenderEntry(current with
            {
                Text = clean,
                Origin = origin,
                LastReinforced = today ?? DateOnly.FromDateTime(DateTime.Now)
            });
            var updated = MemoryMarkdown.ReplaceLine(document.RawText, current.LineIndex, current.Text, replacement);
            return updated is null
                 ? MemoryMutation.Fail("记忆已被修改，请重新读取后重试。")
                : MemoryMutation.Write(updated, current.Id);
        });
    }

    public MemoryWriteResult Delete(MemoryEntry target)
    {
        return Mutate(document =>
        {
            var current = Relocate(document, target);
            if (current is null) return MemoryMutation.NoChange("记忆已不存在。", target.Id);

            var updated = MemoryMarkdown.RemoveLine(document.RawText, current.LineIndex, current.Text);
            return updated is null
                 ? MemoryMutation.Fail("记忆已被修改，请重新读取后重试。")
                : MemoryMutation.Write(updated, current.Id);
        });
    }

    /// <summary>
    /// Re-find an entry in a freshly read document. Ids are matched first, then
    /// the normalized text — a user who deleted the metadata comment by hand
    /// still has a perfectly good memory, and it must stay editable.
    /// </summary>
    private static MemoryEntry? Relocate(MemoryDocument document, MemoryEntry target)
    {
        var byId = document.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var key = MemoryGuards.NormalizeKey(target.Text);
        var matches = document.Entries
            .Where(entry => string.Equals(MemoryGuards.NormalizeKey(entry.Text), key, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Read, mutate, write — with the file's modification stamp re-checked at the
    /// last moment. Changed underneath us? Replay the mutation against the new
    /// text once. Changed again? Give up and say so, because a third attempt is
    /// no more likely to win the race and silently clobbering the user's edit is
    /// the one outcome that must not happen.
    /// </summary>
    private MemoryWriteResult Mutate(Func<MemoryDocument, MemoryMutation> mutation)
    {
        lock (_writeLock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var document = Read();
                var result = mutation(document);
                if (result.NewText is null)
                    return result.Ok
                        ? MemoryWriteResult.Unchanged(result.Message, result.EntryId)
                        : MemoryWriteResult.Fail(result.Message ?? "写入失败。");

                try
                {
                    EnsureDirectory();
                    if (MemoryStamp() != document.LastWriteUtc) continue;   // moved under us: replay once
                    Snapshot(MemoryPath);
                    AtomicWrite(MemoryPath, result.NewText);
                    return MemoryWriteResult.Written(result.Message, result.EntryId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return MemoryWriteResult.Fail("无法写入记忆文件：" + ex.Message);
                }
            }

            return MemoryWriteResult.Fail("记忆文件正在被其他程序修改，本次未写入。");
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Copy the current version aside before overwriting it, newest 20
    /// kept. Cheap enough to do on every write, and the only thing standing
    /// between a stale editor buffer and a lost memory file.</summary>
    private void Snapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(HistoryDirectory);
            var name = Path.GetFileNameWithoutExtension(path)
                + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
                + Path.GetExtension(path);
            File.Copy(path, Path.Combine(HistoryDirectory, name), overwrite: true);

            var stale = new DirectoryInfo(HistoryDirectory)
                .GetFiles(Path.GetFileNameWithoutExtension(path) + ".*")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(HistoryKeep);
            foreach (var file in stale) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing snapshot must not stop the write it was protecting.
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private readonly record struct MemoryMutation(string? NewText, string? Message, string? EntryId, bool Ok)
    {
        public static MemoryMutation Write(string text, string entryId) => new(text, null, entryId, true);
        public static MemoryMutation NoChange(string message, string entryId) => new(null, message, entryId, true);
        public static MemoryMutation Fail(string message) => new(null, message, null, false);
    }
}

public sealed record MemoryWriteResult(bool Ok, bool Changed, string? Message, string? EntryId)
{
    public static MemoryWriteResult Written(string? message, string? entryId) => new(true, true, message, entryId);
    public static MemoryWriteResult Unchanged(string? message, string? entryId) => new(true, false, message, entryId);
    public static MemoryWriteResult Fail(string message) => new(false, false, message, null);
}
