using System.Text;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Attachments;

/// <summary>
/// Capabilities that change how attachment text is presented. The prompt only
/// promises what the model can actually do this turn: telling it to call
/// <c>read_file</c> when the tool is not advertised is worse than saying nothing.
/// </summary>
public sealed record AttachmentPromptOptions(
    bool CanUseReadFile = false,
    bool CanUsePython = false,
    bool CanAnalyzeImage = false,
    int MaxInlineCharsPerFile = AttachedFilePrompt.DefaultInlineCharsPerFile,
    int MaxInlineCharsTotal = AttachedFilePrompt.DefaultInlineCharsTotal)
{
    public static readonly AttachmentPromptOptions Default = new();

    /// <summary>True when some tool can reach the original file on disk.</summary>
    public bool CanOpenOriginal => CanUseReadFile || CanUsePython;

    /// <summary>
    /// Derives the options from the tools this turn will actually advertise.
    /// <paramref name="modelSupportsTools"/> gates every flag because a model
    /// without tool calling never sees the tool definitions, however the user
    /// configured them.
    /// </summary>
    public static AttachmentPromptOptions From(LocalTools.LocalToolOptions? tools, bool modelSupportsTools) =>
        tools is null || !modelSupportsTools
            ? Default
            : Default with
            {
                CanUseReadFile = tools.FileTools,
                CanUsePython = tools.Python?.Enabled == true,
                // Deferred to the tool's own predicate rather than re-derived
                // here: the guidance must never name a tool the host decided not
                // to register, and one predicate cannot disagree with itself.
                CanAnalyzeImage = Tools.Vision.ImageAnalysisTool.IsAvailable(tools)
            };
}

/// <summary>
/// Renders file attachments into the single text part the model sees.
///
/// Three rules drive the format:
/// <list type="number">
/// <item>Content is always inlined, so a file's visibility never depends on the
/// model deciding to call a tool — weak and tool-less models see it too.</item>
/// <item>Truncation is disclosed with exact numbers and a way to get the rest,
/// because a model that does not know it was cut off will answer as if the
/// missing part does not exist.</item>
/// <item>Every failure degrades to a model-visible note. A file is never
/// silently dropped from the request.</item>
/// </list>
/// </summary>
public static class AttachedFilePrompt
{
    /// <summary>Per-file inline ceiling. Sized so a typical paper or report
    /// arrives whole rather than cut off at the introduction.</summary>
    public const int DefaultInlineCharsPerFile = 30_000;

    /// <summary>Ceiling across all files in one message, so attaching a folder's
    /// worth of documents cannot swallow the whole context window.</summary>
    public const int DefaultInlineCharsTotal = 60_000;

    private const string CloseTag = "</attached_file>";

    public static string? Build(IReadOnlyList<Attachment> files, AttachmentPromptOptions? options = null)
    {
        if (files.Count == 0) return null;
        var opts = options ?? AttachmentPromptOptions.Default;

        var sb = new StringBuilder();
        sb.Append("[附件] 用户随消息上传了以下文件：\n");

        var remaining = Math.Max(0, opts.MaxInlineCharsTotal);
        var anyTruncated = false;
        var anyWorkspaceFile = false;
        var anyPdf = false;

        foreach (var file in files)
        {
            if (file.IsWorkspaceFile) anyWorkspaceFile = true;
            if (IsPdf(file)) anyPdf = true;

            sb.Append('\n').Append(RenderFile(file, opts, ref remaining, ref anyTruncated)).Append('\n');
        }

        AppendGuidance(sb, opts, anyTruncated, anyWorkspaceFile, anyPdf);
        return sb.ToString();
    }

    private static string RenderFile(
        Attachment file,
        AttachmentPromptOptions options,
        ref int remaining,
        ref bool anyTruncated)
    {
        var attributes = new List<(string Key, string Value)>
        {
            ("name", file.DisplayName),
            ("mime", file.MimeType)
        };

        if (file.IsUnavailable)
        {
            attributes.Add(("unavailable", file.UnavailableReason!));
            return RenderSelfClosing(attributes);
        }

        var text = file.Text;
        if (text?.PageCount is { } pages) attributes.Add(("pages", pages.ToString()));
        if (!string.IsNullOrWhiteSpace(file.WorkspaceRelativePath))
            attributes.Add(("path", file.WorkspaceRelativePath!));

        if (text is null || !text.HasBody)
        {
            attributes.Add(("note", text?.Note ?? "无法从该文件中提取文字。"));
            return RenderSelfClosing(attributes);
        }

        var body = text.Body!;
        attributes.Add(("chars", body.Length.ToString()));

        var allowance = Math.Min(options.MaxInlineCharsPerFile, remaining);
        if (allowance <= 0)
        {
            attributes.Add(("truncated", $"0/{body.Length}"));
            AppendTextPath(attributes, text);
            attributes.Add(("note", "本轮附件文本预算已用尽，正文未内联；请按上面的路径自行读取。"));
            anyTruncated = true;
            return RenderSelfClosing(attributes);
        }

        string inlined;
        if (body.Length <= allowance)
        {
            inlined = body;
            remaining -= body.Length;
        }
        else
        {
            inlined = CutAtBoundary(body, allowance);
            remaining -= inlined.Length;
            anyTruncated = true;
            attributes.Add(("truncated", $"{inlined.Length}/{body.Length}"));
            AppendTextPath(attributes, text);
        }

        if (!string.IsNullOrWhiteSpace(text.Note)) attributes.Add(("note", text.Note!));

        var sb = new StringBuilder();
        sb.Append(RenderOpenTag(attributes)).Append('\n');
        sb.Append(Neutralize(inlined)).Append('\n');
        sb.Append(CloseTag);
        return sb.ToString();
    }

    private static void AppendTextPath(List<(string Key, string Value)> attributes, AttachmentText text)
    {
        if (!string.IsNullOrWhiteSpace(text.TextFileRelativePath))
            attributes.Add(("text_path", text.TextFileRelativePath!));
    }

    private static void AppendGuidance(
        StringBuilder sb,
        AttachmentPromptOptions options,
        bool anyTruncated,
        bool anyWorkspaceFile,
        bool anyPdf)
    {
        sb.Append("\n说明：\n");

        var canReachFiles = anyWorkspaceFile && options.CanOpenOriginal;

        // Stated unconditionally, including when extraction fully succeeded: a
        // PDF with a clean text layer still hides its figures, plotted curves and
        // equation images, and a model that is not told so will answer as if the
        // text were the whole document.
        //
        // Inlining and tool use are not alternatives. The text is here so the
        // answer never depends on a tool call, but when tools *are* available
        // the caveat should point at them — "我只能看到文本" is the right thing
        // to say only when it is actually true.
        //
        // Gated on Python specifically, not on file access in general: pulling a
        // figure out of a PDF takes code, and read_file cannot look at a picture
        // even when it can reach the file.
        var canRecoverVisuals = anyWorkspaceFile && options.CanUsePython;
        sb.Append("- 以上是从文件中提取的纯文本。文件内的图片、图表、公式、扫描页、手写内容和复杂表格排版无法提取，"
                  + "可能存在你看不到的信息；若用户的问题涉及这些内容，");
        sb.Append(canRecoverVisuals
            ? "先按下面的办法从原件里把它们取出来，确实取不到再说明你只能看到文本部分。\n"
            : "请主动说明你只能看到文本部分。\n");

        if (canReachFiles)
        {
            sb.Append("- 文件原件已保存在当前对话的工作目录中，可用上面 path 属性给出的相对路径直接访问");
            sb.Append(options.CanUseReadFile && options.CanUsePython
                ? "（read_file 读文本，execute_python_code 处理原件）。\n"
                : options.CanUseReadFile ? "（read_file）。\n" : "（execute_python_code）。\n");
        }

        if (anyTruncated)
        {
            if (options.CanUseReadFile)
                sb.Append("- 标注了 truncated 的文件只内联了一部分。用 read_file 读取 text_path（没有 text_path 时读 path），"
                          + "配合 offset 参数继续往下读，不要凭截断处的内容下结论。\n");
            else if (options.CanUsePython)
                sb.Append("- 标注了 truncated 的文件只内联了一部分。用 execute_python_code 打开 text_path"
                          + "（没有 text_path 时打开 path）读取其余内容，不要凭截断处的内容下结论。\n");
            else
                sb.Append("- 标注了 truncated 的文件只内联了一部分，其余内容你看不到。"
                          + "如果回答依赖被截断的部分，请直接告诉用户。\n");
        }

        if (options.CanUsePython && anyWorkspaceFile)
        {
            sb.Append("- 需要表格数据、页面级操作，或要取出文件里的图片时，用 execute_python_code 直接处理原件");
            sb.Append(anyPdf
                ? "：PDF 参考 pdf 技能（内置 pypdf/fpdf2，可按页提取文字、拆分合并、取出内嵌图片），Word/Excel/PPT 同理有对应技能。\n"
                : "，相应格式的处理方式参考对应技能。\n");
        }

        if (options.CanAnalyzeImage)
        {
            // The pairing matters more than either half: extracting a figure to
            // disk accomplishes nothing on its own, because the model still
            // cannot see it. Spell out the second step in the same breath.
            sb.Append(options.CanUsePython && anyWorkspaceFile
                ? "- 取出来的图片存进工作目录后，用 analyze_image 加文件名就能看它的内容；"
                : "- 工作目录里的图片可以用 analyze_image 加文件名查看；");
            sb.Append("附带 query 说明你要看什么（读哪条曲线、取哪个数值、看哪一块），比不带问题的泛泛描述有用得多。\n");
        }
    }

    /// <summary>
    /// Cuts on a line boundary when one is close to the limit, so the inlined
    /// text does not end mid-token. Falls back to a hard cut for content without
    /// line breaks (minified JSON, single-paragraph exports).
    /// </summary>
    private static string CutAtBoundary(string body, int limit)
    {
        var head = body[..limit];
        var lastBreak = head.LastIndexOf('\n');
        return lastBreak > limit - 500 && lastBreak > 0 ? head[..lastBreak] : head;
    }

    /// <summary>Keeps document content from closing the wrapper it sits in —
    /// an attachment must not be able to forge the end of its own block.</summary>
    private static string Neutralize(string body) =>
        body.Contains(CloseTag, StringComparison.OrdinalIgnoreCase)
            ? body.Replace(CloseTag, "<∕attached_file>", StringComparison.OrdinalIgnoreCase)
            : body;

    private static string RenderOpenTag(List<(string Key, string Value)> attributes) =>
        "<attached_file" + RenderAttributes(attributes) + ">";

    private static string RenderSelfClosing(List<(string Key, string Value)> attributes) =>
        "<attached_file" + RenderAttributes(attributes) + " />";

    private static string RenderAttributes(List<(string Key, string Value)> attributes)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in attributes)
            sb.Append(' ').Append(key).Append("=\"").Append(EscapeAttribute(value)).Append('"');
        return sb.ToString();
    }

    private static string EscapeAttribute(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static bool IsPdf(Attachment file) =>
        file.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || file.DisplayName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
