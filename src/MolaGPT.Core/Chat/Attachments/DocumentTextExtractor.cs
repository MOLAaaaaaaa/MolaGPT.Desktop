using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Chat.Attachments;

/// <summary>
/// Result of turning an attachment's bytes into prompt text.
/// <see cref="Text"/> and <see cref="Note"/> are not exclusive: a PDF can yield
/// text <em>and</em> a note explaining that only part of it came through.
/// </summary>
public sealed record DocumentExtraction(
    string? Text,
    int? PageCount = null,
    string? Note = null)
{
    public int TotalChars => Text?.Length ?? 0;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public static DocumentExtraction Failed(string note) => new(null, null, note);
}

/// <summary>
/// Converts an attachment's bytes to plain text so its content reaches the model
/// inline, without depending on the model choosing to call a tool — weak models
/// and tool-less models have to see it too.
///
/// Results are memoised on the SHA-256 of the content. Because the local
/// attachment store is content-addressed as well, the hash is a perfect cache
/// key: it can never go stale, so re-sending the same document across turns
/// costs one dictionary lookup instead of a full re-parse.
/// </summary>
public static class DocumentTextExtractor
{
    private const int MaxCacheEntries = 32;

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, DocumentExtraction> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> CacheOrder = new();

    public static DocumentExtraction Extract(byte[]? bytes, string? mimeType, string? fileName)
    {
        if (bytes is not { Length: > 0 })
            return DocumentExtraction.Failed("文件为空。");

        var key = Convert.ToHexString(SHA256.HashData(bytes));
        if (TryGetCached(key, out var cached)) return cached;

        var result = ExtractCore(bytes, mimeType, fileName);
        Store(key, result);
        return result;
    }

    private static DocumentExtraction ExtractCore(byte[] bytes, string? mimeType, string? fileName)
    {
        var kind = AttachmentMime.ClassifyDocument(mimeType, fileName, bytes);
        try
        {
            return kind switch
            {
                AttachmentDocumentKind.Text => new DocumentExtraction(DecodeText(bytes)),
                AttachmentDocumentKind.Docx => Wrap(DocxTextExtractor.Extract(bytes), "Word 文档中未找到可提取的文字。"),
                AttachmentDocumentKind.Xlsx => Wrap(XlsxTextExtractor.Extract(bytes), "工作簿中未找到可提取的单元格内容。"),
                AttachmentDocumentKind.Pptx => Wrap(PptxTextExtractor.Extract(bytes), "演示文稿中未找到可提取的文字。"),
                AttachmentDocumentKind.Pdf => PdfTextExtractor.Extract(bytes),
                AttachmentDocumentKind.LegacyOffice => DocumentExtraction.Failed(
                    "这是 2007 年以前的 Office 二进制格式（.doc/.xls/.ppt），无法可靠地提取文字。"
                    + "请用 Office 另存为 .docx/.xlsx/.pptx 后重新上传。"),
                _ => DocumentExtraction.Failed("这是二进制文件，无法作为文本读取。")
            };
        }
        catch (Exception ex)
        {
            return DocumentExtraction.Failed($"提取文字时出错：{ex.Message}");
        }
    }

    private static DocumentExtraction Wrap(string text, string emptyNote) =>
        string.IsNullOrWhiteSpace(text)
            ? DocumentExtraction.Failed(emptyNote)
            : new DocumentExtraction(text);

    /// <summary>Decodes UTF-8, dropping a BOM. Classification already proved the
    /// bytes are valid UTF-8, so a lenient decode here cannot produce mojibake.</summary>
    private static string DecodeText(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static bool TryGetCached(string key, out DocumentExtraction result)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var found))
            {
                result = default!;
                return false;
            }
            CacheOrder.Remove(key);
            CacheOrder.AddFirst(key);
            result = found;
            return true;
        }
    }

    private static void Store(string key, DocumentExtraction result)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(key)) CacheOrder.Remove(key);
            Cache[key] = result;
            CacheOrder.AddFirst(key);

            while (CacheOrder.Count > MaxCacheEntries)
            {
                var oldest = CacheOrder.Last!.Value;
                CacheOrder.RemoveLast();
                Cache.Remove(oldest);
            }
        }
    }

    internal static void ClearCacheForTests()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }
}
