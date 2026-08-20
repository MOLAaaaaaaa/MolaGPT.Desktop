using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MolaGPT.Core.Chat.Attachments;

/// <summary>
/// PDF text-layer extraction. Reads the text the document already carries — no
/// OCR, no rasterising. A scanned PDF therefore yields nothing, and that fact is
/// reported to the model rather than papered over.
///
/// Pages are labelled so a model that cites "第 3 页" is citing the same page the
/// user sees, and so a truncated extraction still tells the reader where it
/// stopped.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>Page ceiling. Well past any document a chat attachment plausibly
    /// carries, and low enough that a malformed file cannot spin for minutes.</summary>
    public const int MaxPages = 500;

    public static DocumentExtraction Extract(byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            var totalPages = document.NumberOfPages;
            var pageLimit = Math.Min(totalPages, MaxPages);

            var sb = new StringBuilder();
            var pagesWithText = 0;

            for (var number = 1; number <= pageLimit; number++)
            {
                string pageText;
                try
                {
                    pageText = ContentOrderTextExtractor.GetText(document.GetPage(number))?.Trim() ?? string.Empty;
                }
                catch (Exception)
                {
                    // One unreadable page (broken font, malformed content stream)
                    // must not cost the caller the rest of the document.
                    continue;
                }

                if (pageText.Length == 0) continue;

                pagesWithText++;
                sb.Append("\n--- 第 ").Append(number).Append(" 页 ---\n").Append(pageText).Append('\n');

                if (sb.Length > OfficeOpenXml.MaxExtractedChars) break;
            }

            if (pagesWithText == 0)
            {
                return new DocumentExtraction(
                    null,
                    totalPages,
                    "未提取到文字层，可能是扫描件或纯图片 PDF。");
            }

            var note = BuildCoverageNote(totalPages, pageLimit, pagesWithText);
            return new DocumentExtraction(sb.ToString().Trim('\n'), totalPages, note);
        }
        catch (Exception ex)
        {
            return DocumentExtraction.Failed(
                $"无法解析该 PDF（{ex.Message}）。文件可能已加密或损坏。");
        }
    }

    /// <summary>
    /// Reports partial coverage. "Some pages produced no text" is the signal that
    /// a document mixes real text with scanned inserts — the model needs to know
    /// that before it concludes something is absent from the document.
    /// </summary>
    private static string? BuildCoverageNote(int totalPages, int pageLimit, int pagesWithText)
    {
        var notes = new List<string>();
        if (pageLimit < totalPages)
            notes.Add($"仅提取了前 {pageLimit} 页（共 {totalPages} 页）");
        if (pagesWithText < pageLimit)
            notes.Add($"{pageLimit - pagesWithText} 页没有文字层（可能是扫描页或整页图片）");
        return notes.Count == 0 ? null : string.Join("；", notes) + "。";
    }
}
