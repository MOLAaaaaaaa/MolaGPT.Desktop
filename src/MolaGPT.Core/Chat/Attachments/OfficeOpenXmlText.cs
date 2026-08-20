using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MolaGPT.Core.Chat.Attachments;

/// <summary>
/// Shared plumbing for reading OOXML packages (.docx/.xlsx/.pptx). These are
/// plain zip archives of XML parts, so the whole family is handled with
/// <see cref="ZipArchive"/> + <see cref="XDocument"/> — no third-party
/// dependency, and nothing that can execute embedded content.
/// </summary>
internal static class OfficeOpenXml
{
    /// <summary>Safety ceiling shared by every extractor. A pathological
    /// workbook can hold millions of cells; we stop long before the prompt
    /// budget (and the caller's truncation) would ever matter.</summary>
    internal const int MaxExtractedChars = 2_000_000;

    internal static readonly XmlReaderSettings ReaderSettings = new()
    {
        // OOXML parts never legitimately reference external entities; refusing
        // them keeps a malicious document from reaching the network or disk.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false
    };

    internal static XDocument? ReadXml(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath);
        if (entry is null) return null;
        return ReadXml(entry);
    }

    internal static XDocument? ReadXml(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, ReaderSettings);
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Entries directly under <paramref name="prefix"/>, ordered by the
    /// trailing number in their name so slide10 sorts after slide9.</summary>
    internal static IEnumerable<ZipArchiveEntry> OrderedParts(ZipArchive zip, string prefix, string suffix) =>
        zip.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && e.FullName.IndexOf('/', prefix.Length) < 0)
            .OrderBy(e => TrailingNumber(e.FullName))
            .ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);

    internal static int TrailingNumber(string name)
    {
        var end = name.LastIndexOf('.');
        if (end < 0) end = name.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
        return start == end ? int.MaxValue : int.Parse(name[start..end]);
    }

    /// <summary>Collapses the runs of blank lines that fall out of paragraph-wise
    /// extraction and trims trailing whitespace on every line.</summary>
    internal static string Tidy(StringBuilder sb)
    {
        var lines = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new StringBuilder(sb.Length);
        var blankRun = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (++blankRun > 1) continue;
            }
            else
            {
                blankRun = 0;
            }
            result.Append(line).Append('\n');
        }
        return result.ToString().Trim('\n');
    }
}

/// <summary>
/// Word (.docx) to Markdown. Headings, lists, bold/italic and tables survive the
/// trip because structure is what lets a model answer "what does section 3 say"
/// — a flat text dump loses exactly the cues it needs.
/// </summary>
public static class DocxTextExtractor
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static string Extract(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var document = OfficeOpenXml.ReadXml(zip, "word/document.xml");
        var body = document?.Root?.Element(W + "body");
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var node in body.Elements())
        {
            if (sb.Length > OfficeOpenXml.MaxExtractedChars) break;

            if (node.Name == W + "p")
                AppendParagraph(sb, node);
            else if (node.Name == W + "tbl")
                AppendTable(sb, node);
        }

        return OfficeOpenXml.Tidy(sb);
    }

    private static void AppendParagraph(StringBuilder sb, XElement paragraph)
    {
        var text = InlineText(paragraph);
        var properties = paragraph.Element(W + "pPr");

        if (string.IsNullOrWhiteSpace(text))
        {
            sb.Append('\n');
            return;
        }

        var headingLevel = HeadingLevel(properties);
        if (headingLevel > 0)
        {
            sb.Append('\n').Append('#', headingLevel).Append(' ').Append(text).Append("\n\n");
            return;
        }

        if (properties?.Element(W + "numPr") is { } numbering)
        {
            var indent = ReadInt(numbering.Element(W + "ilvl")) ?? 0;
            sb.Append(' ', Math.Clamp(indent, 0, 6) * 2).Append("- ").Append(text).Append('\n');
            return;
        }

        sb.Append(text).Append("\n\n");
    }

    private static void AppendTable(StringBuilder sb, XElement table)
    {
        var rows = table.Elements(W + "tr").ToList();
        if (rows.Count == 0) return;

        sb.Append('\n');
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Elements(W + "tc")
                .Select(tc => string.Join(" ", tc.Elements(W + "p").Select(InlineText))
                    .Replace('|', '∣')  // keep a literal pipe from breaking the table
                    .Trim())
                .ToList();
            if (cells.Count == 0) continue;

            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (r == 0)
                sb.Append("| ").Append(string.Join(" | ", cells.Select(_ => "---"))).Append(" |\n");
        }
        sb.Append('\n');
    }

    /// <summary>
    /// Renders a paragraph's runs, merging neighbours that share formatting so
    /// the output is <c>**one phrase**</c> rather than <c>**one****phrase**</c>.
    /// </summary>
    private static string InlineText(XElement paragraph)
    {
        var segments = new List<(string Text, bool Bold, bool Italic)>();

        foreach (var run in paragraph.Descendants(W + "r"))
        {
            var runText = new StringBuilder();
            foreach (var node in run.Elements())
            {
                if (node.Name == W + "t") runText.Append(node.Value);
                else if (node.Name == W + "tab") runText.Append('\t');
                else if (node.Name == W + "br" || node.Name == W + "cr") runText.Append(' ');
            }
            if (runText.Length == 0) continue;

            var properties = run.Element(W + "rPr");
            var bold = IsToggleOn(properties?.Element(W + "b"));
            var italic = IsToggleOn(properties?.Element(W + "i"));

            if (segments.Count > 0 && segments[^1].Bold == bold && segments[^1].Italic == italic)
                segments[^1] = (segments[^1].Text + runText, bold, italic);
            else
                segments.Add((runText.ToString(), bold, italic));
        }

        var sb = new StringBuilder();
        foreach (var (text, bold, italic) in segments)
        {
            // Emphasis only wraps the visible core; leading/trailing spaces must
            // stay outside the markers or Markdown drops the emphasis entirely.
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || (!bold && !italic))
            {
                sb.Append(text);
                continue;
            }

            var marker = bold && italic ? "***" : bold ? "**" : "*";
            var leading = text[..(text.Length - text.TrimStart().Length)];
            var trailing = text[(text.TrimEnd().Length)..];
            sb.Append(leading).Append(marker).Append(trimmed).Append(marker).Append(trailing);
        }

        return sb.ToString().Trim();
    }

    private static int HeadingLevel(XElement? properties)
    {
        var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        if (string.IsNullOrWhiteSpace(style)) return 0;

        var normalized = style!.Replace(" ", string.Empty).ToLowerInvariant();
        if (normalized is "title") return 1;
        if (normalized is "subtitle") return 2;
        if (!normalized.StartsWith("heading", StringComparison.Ordinal)) return 0;

        var digits = normalized["heading".Length..];
        return int.TryParse(digits, out var level) ? Math.Clamp(level, 1, 6) : 0;
    }

    /// <summary>OOXML toggle properties are on when present unless explicitly
    /// switched off with <c>w:val="0"/"false"</c>.</summary>
    private static bool IsToggleOn(XElement? toggle)
    {
        if (toggle is null) return false;
        var value = toggle.Attribute(W + "val")?.Value;
        return value is null or "1" or "true" or "on";
    }

    private static int? ReadInt(XElement? element)
    {
        var value = element?.Attribute(W + "val")?.Value;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Excel (.xlsx) to per-sheet Markdown tables. Values are emitted as stored:
/// number formats are not applied, so a date cell reads as its serial number.
/// The prompt wrapper says so rather than silently presenting it as fact.
/// </summary>
public static class XlsxTextExtractor
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const int MaxRowsPerSheet = 5000;

    public static string Extract(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var sharedStrings = ReadSharedStrings(zip);
        var sb = new StringBuilder();

        foreach (var (name, partPath) in ReadSheetIndex(zip))
        {
            if (sb.Length > OfficeOpenXml.MaxExtractedChars) break;

            var sheet = OfficeOpenXml.ReadXml(zip, partPath);
            var rows = sheet?.Root?.Element(S + "sheetData")?.Elements(S + "row").ToList();
            if (rows is null) continue;

            sb.Append("\n## 工作表：").Append(name).Append("\n\n");
            if (rows.Count == 0)
            {
                sb.Append("(空工作表)\n");
                continue;
            }

            var emitted = 0;
            foreach (var row in rows)
            {
                if (emitted >= MaxRowsPerSheet)
                {
                    sb.Append($"\n[该工作表还有 {rows.Count - emitted} 行未提取]\n");
                    break;
                }

                var cells = ReadRow(row, sharedStrings);
                if (cells.Count == 0) continue;

                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                if (emitted == 0)
                    sb.Append("| ").Append(string.Join(" | ", cells.Select(_ => "---"))).Append(" |\n");
                emitted++;
            }
        }

        return OfficeOpenXml.Tidy(sb);
    }

    /// <summary>
    /// Maps sheet display names to their part paths through workbook.xml.rels.
    /// Falls back to positional worksheet parts when the relationship graph is
    /// missing, which is common in files written by non-Microsoft tools.
    /// </summary>
    private static IEnumerable<(string Name, string Path)> ReadSheetIndex(ZipArchive zip)
    {
        var workbook = OfficeOpenXml.ReadXml(zip, "xl/workbook.xml");
        var rels = OfficeOpenXml.ReadXml(zip, "xl/_rels/workbook.xml.rels");

        var targets = rels?.Root?.Elements(Rel + "Relationship")
            .Where(e => e.Attribute("Id")?.Value is { Length: > 0 })
            .ToDictionary(
                e => e.Attribute("Id")!.Value,
                e => NormalizeTarget(e.Attribute("Target")?.Value),
                StringComparer.Ordinal);

        var sheets = workbook?.Root?.Element(S + "sheets")?.Elements(S + "sheet").ToList();
        if (sheets is { Count: > 0 } && targets is { Count: > 0 })
        {
            foreach (var sheet in sheets)
            {
                var id = sheet.Attribute(R + "id")?.Value;
                var name = sheet.Attribute("name")?.Value ?? "Sheet";
                if (id is not null && targets.TryGetValue(id, out var path) && path is { Length: > 0 })
                    yield return (name, path);
            }
            yield break;
        }

        foreach (var entry in OfficeOpenXml.OrderedParts(zip, "xl/worksheets/", ".xml"))
            yield return (Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName);
    }

    private static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        var value = target!.Replace('\\', '/').TrimStart('/');
        if (value.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return value;
        return "xl/" + value;
    }

    private static List<string> ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var cells = new List<string>();
        var expected = 1;

        foreach (var cell in row.Elements(S + "c"))
        {
            var column = ColumnIndex(cell.Attribute("r")?.Value);
            if (column > 0)
            {
                // Blank cells are omitted from the XML; re-insert them so column
                // alignment (and therefore the header mapping) is preserved.
                while (expected < column)
                {
                    cells.Add(string.Empty);
                    expected++;
                }
            }

            cells.Add(CellText(cell, sharedStrings));
            expected++;
        }

        while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        string value;

        if (type == "s")
        {
            var raw = cell.Element(S + "v")?.Value;
            value = int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }
        else if (type == "inlineStr")
        {
            value = string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);
        }
        else if (type == "b")
        {
            value = cell.Element(S + "v")?.Value == "1" ? "TRUE" : "FALSE";
        }
        else
        {
            value = cell.Element(S + "v")?.Value ?? string.Empty;
        }

        return value.Replace('|', '∣').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>Converts the letter prefix of a cell reference ("BC12") to a
    /// 1-based column index.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return index;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive zip)
    {
        var document = OfficeOpenXml.ReadXml(zip, "xl/sharedStrings.xml");
        var items = document?.Root?.Elements(S + "si");
        if (items is null) return Array.Empty<string>();

        // A shared string can be split across formatting runs; concatenating all
        // t descendants reassembles it (and skips rPh phonetic hints, which are
        // not part of the visible value).
        return items
            .Select(si => string.Concat(si.Descendants(S + "t")
                .Where(t => t.Parent?.Name != S + "rPh")
                .Select(t => t.Value)))
            .ToList();
    }
}

/// <summary>PowerPoint (.pptx) to per-slide text, speaker notes included.</summary>
public static class PptxTextExtractor
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static string Extract(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var sb = new StringBuilder();
        var slideNumber = 0;

        foreach (var entry in OfficeOpenXml.OrderedParts(zip, "ppt/slides/", ".xml"))
        {
            if (sb.Length > OfficeOpenXml.MaxExtractedChars) break;
            slideNumber++;

            sb.Append("\n## 第 ").Append(slideNumber).Append(" 页\n\n");
            var slide = OfficeOpenXml.ReadXml(entry);
            AppendParagraphs(sb, slide);

            var notes = OfficeOpenXml.ReadXml(zip, $"ppt/notesSlides/notesSlide{OfficeOpenXml.TrailingNumber(entry.FullName)}.xml");
            if (notes is null) continue;

            var notesText = new StringBuilder();
            AppendParagraphs(notesText, notes);
            var trimmed = OfficeOpenXml.Tidy(notesText);
            if (trimmed.Length > 0)
                sb.Append("\n> 备注：").Append(trimmed.Replace("\n", "\n> ")).Append('\n');
        }

        return OfficeOpenXml.Tidy(sb);
    }

    private static void AppendParagraphs(StringBuilder sb, XDocument? part)
    {
        if (part?.Root is null) return;
        foreach (var paragraph in part.Root.Descendants(A + "p"))
        {
            var text = string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value)).Trim();
            if (text.Length > 0) sb.Append(text).Append('\n');
        }
    }
}
