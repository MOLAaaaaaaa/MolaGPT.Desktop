using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using MemorySection = MolaGPT.Core.Personalization.MemorySection;

namespace MolaGPT.Core.Memory;

/// <summary>
/// MEMORY.md is a plain Markdown file the user is expected to open and edit, so
/// the parser's contract is the other way round from a normal serializer: it has
/// to accept whatever a person would reasonably type, and it must never be used
/// to re-render the file.
///
/// A bare <c>- 一句话</c> under a known heading is a complete entry. The trailing
/// HTML comment is optional metadata (id, confidence, origin, last reinforced);
/// when it is missing the line counts as hand-written — permanent, full
/// confidence, and off-limits to automatic learning. That is the whole point of
/// choosing Markdown: typing a line in an editor has to be a first-class way to
/// add a memory, not a degraded one.
/// </summary>
public static partial class MemoryMarkdown
{
    public const string FileHeader = "# MEMORY";

    [GeneratedRegex(@"^\s{0,3}##\s+(?<heading>.+?)\s*$")]
    private static partial Regex SectionHeadingRegex();

    [GeneratedRegex(@"^\s*[-*]\s+(?<body>.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"<!--\s*m:(?<id>[A-Za-z0-9]+)(?<rest>[^>]*?)-->\s*$")]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"\b(?<key>[a-z]+):(?<value>[^\s]+)")]
    private static partial Regex MetaFieldRegex();

    public static IReadOnlyList<MemoryEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<MemoryEntry>();

        var entries = new List<MemoryEntry>();
        var lines = SplitLines(text);
        MemorySection? current = null;
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // A fenced block in a memory file is unusual but legal (a user
            // pasting a snippet into 明确的禁止项, say). Bullets inside it are
            // content, not entries.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            if (SectionHeadingRegex().Match(line) is { Success: true } heading)
            {
                current = MemorySectionRules.TryParse(heading.Groups["heading"].Value, out var parsed)
                    ? parsed
                    : null;   // an unknown heading: its bullets are the user's own prose, left alone
                continue;
            }

            if (current is not { } section) continue;
            if (BulletRegex().Match(line) is not { Success: true } bullet) continue;

            var body = bullet.Groups["body"].Value;
            var entry = ParseEntry(body, section, i);
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    private static MemoryEntry? ParseEntry(string body, MemorySection section, int lineIndex)
    {
        var id = string.Empty;
        var confidence = 1d;
        var origin = MemoryOrigin.Manual;
        DateOnly? reinforced = null;
        string? profileKey = null;
        string? topicId = null;

        var meta = MetaRegex().Match(body);
        var text = (meta.Success ? body[..meta.Index] : body).Trim();
        if (text.Length == 0) return null;

        if (meta.Success)
        {
            id = meta.Groups["id"].Value;
            foreach (Match field in MetaFieldRegex().Matches(meta.Groups["rest"].Value))
            {
                var value = field.Groups["value"].Value;
                switch (field.Groups["key"].Value)
                {
                    case "c":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                            confidence = Math.Clamp(parsed, 0, 1);
                        break;
                    case "o":
                        if (Enum.TryParse<MemoryOrigin>(value, ignoreCase: true, out var parsedOrigin))
                            origin = parsedOrigin;
                        break;
                    case "t":
                        if (DateOnly.TryParseExact(value, "yy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var parsedDate))
                            reinforced = parsedDate;
                        break;
                    case "p":
                        if (MemoryProfile.IsKnownKey(value)) profileKey = value;
                        break;
                    case "topic":
                        topicId = value;
                        break;
                }
            }
        }

        return new MemoryEntry
        {
            Id = string.IsNullOrEmpty(id) ? StableId(text) : id,
            Section = section,
            Text = text,
            Confidence = confidence,
            Origin = origin,
            LastReinforced = reinforced,
            ProfileKey = profileKey,
            TopicId = topicId,
            LineIndex = lineIndex
        };
    }

    /// <summary>
    /// The id for a line that has no metadata comment. Derived from the text so
    /// that the same hand-written line keeps the same id across reads — the
    /// memory page needs a stable key, and writing an id back into the user's
    /// file just to have one would be editing his file for our own convenience.
    /// </summary>
    public static string StableId(string text)
    {
        var normalized = MemoryGuards.NormalizeKey(text);
        var hash = 2166136261u;
        foreach (var ch in normalized)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture)[..4];
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..4];

    /// <summary>Render one bullet. Manual entries get no metadata comment at
    /// all: what the user typed and what we write back for him should look the
    /// same in his editor.</summary>
    public static string RenderEntry(MemoryEntry entry)
    {
        var line = new StringBuilder("- ").Append(entry.Text.Trim());
        if (entry.Origin == MemoryOrigin.Manual && entry.ProfileKey is null && entry.TopicId is null) return line.ToString();

        line.Append(" <!--m:").Append(entry.Id);
        line.Append(" c:").Append(entry.Confidence.ToString("0.##", CultureInfo.InvariantCulture));
        line.Append(" o:").Append(entry.Origin.ToString().ToLowerInvariant());
        if (entry.LastReinforced is { } stamp)
            line.Append(" t:").Append(stamp.ToString("yy-MM-dd", CultureInfo.InvariantCulture));
        if (entry.ProfileKey is { } key) line.Append(" p:").Append(key);
        if (entry.TopicId is { } topic) line.Append(" topic:").Append(topic);
        line.Append("-->");
        return line.ToString();
    }

    /// <summary>Strip the metadata comments for injection. Internal ids,
    /// confidences and origins are ours, not the model's business.</summary>
    public static string StripMeta(string line) =>
        MetaRegex().Replace(line, string.Empty).TrimEnd();

    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    /// <summary>
    /// Insert a bullet at the end of its section, creating the heading when the
    /// file does not have it yet. Returns the new file text; everything outside
    /// the touched lines is preserved byte for byte.
    /// </summary>
    public static string InsertEntry(string rawText, MemoryEntry entry)
    {
        var lines = SplitLines(string.IsNullOrEmpty(rawText) ? FileHeader + "\n" : rawText).ToList();
        var rendered = RenderEntry(entry);
        var heading = "## " + MemorySectionRules.Heading(entry.Section);

        var headingIndex = lines.FindIndex(line =>
            SectionHeadingRegex().Match(line) is { Success: true } match
            && string.Equals(match.Groups["heading"].Value.Trim(),
                MemorySectionRules.Heading(entry.Section), StringComparison.Ordinal));

        if (headingIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add(string.Empty);
            lines.Add(heading);
            lines.Add(rendered);
            lines.Add(string.Empty);
            return string.Join("\n", lines);
        }

        // Last line that still belongs to this section: walk to the next heading,
        // then back up over trailing blanks so the bullet joins the list instead
        // of landing after the gap that separates two sections.
        var insertAt = lines.Count;
        for (var i = headingIndex + 1; i < lines.Count; i++)
        {
            if (!SectionHeadingRegex().IsMatch(lines[i])) continue;
            insertAt = i;
            break;
        }
        while (insertAt - 1 > headingIndex && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;

        lines.Insert(insertAt, rendered);
        return string.Join("\n", lines);
    }

    /// <summary>Replace exactly one line. Null when the anchor no longer holds —
    /// the caller re-reads and retries rather than writing over a moved line.</summary>
    public static string? ReplaceLine(string rawText, int lineIndex, string expectedText, string replacement)
    {
        var lines = SplitLines(rawText);
        if (lineIndex < 0 || lineIndex >= lines.Length) return null;
        if (!LineCarries(lines[lineIndex], expectedText)) return null;
        lines[lineIndex] = replacement;
        return string.Join("\n", lines);
    }

    public static string? RemoveLine(string rawText, int lineIndex, string expectedText)
    {
        var lines = SplitLines(rawText).ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count) return null;
        if (!LineCarries(lines[lineIndex], expectedText)) return null;
        lines.RemoveAt(lineIndex);
        return string.Join("\n", lines);
    }

    private static bool LineCarries(string line, string expectedText)
    {
        var bullet = BulletRegex().Match(line);
        if (!bullet.Success) return false;
        var body = MetaRegex().Replace(bullet.Groups["body"].Value, string.Empty).Trim();
        return string.Equals(body, expectedText.Trim(), StringComparison.Ordinal);
    }

    // ---- profile.md --------------------------------------------------------

    public static MemoryProfile ParseProfile(string? text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return new MemoryProfile(fields);

        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().TrimStart('-', '*', ' ');
            if (!MemoryProfile.IsKnownKey(key)) continue;
            var value = line[(colon + 1)..].Trim();
            if (value.Length > 0) fields[key] = value;
        }
        return new MemoryProfile(fields);
    }

    public static string RenderProfile(MemoryProfile profile)
    {
        var sb = new StringBuilder("# 个人资料\n\n");
        foreach (var key in MemoryProfile.Keys)
        {
            var value = profile.Get(key);
            if (value is null) continue;
            sb.Append(key).Append(": ").Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
