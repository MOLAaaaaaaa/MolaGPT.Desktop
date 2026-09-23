using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MolaGPT.Core.Models;
using MolaGPT.Presentation.Artifacts;

namespace MolaGPT.ViewModels;

/// <summary>A queued 「基于此修改」 in the composer: which artifact, which version.</summary>
public sealed class ArtifactReferenceViewModel(ArtifactItemViewModel item, int versionIndex)
{
    public ArtifactItemViewModel Item { get; } = item;

    /// <summary>-1 for a working-directory file, which has no versions.</summary>
    public int VersionIndex { get; } = versionIndex;

    public string Label => Item.IsFence && Item.Versions.Count > 1 ? $"{Item.Title} · v{VersionIndex + 1}" : Item.Title;
    public string Badge => Item.Extension;
    public string ToolTip => $"下一条消息会针对「{Label}」";

    public ArtifactRefChip ToChip() =>
        new(Item.Title, Item.Extension, VersionIndex + 1, Item.IsFence ? Item.Versions.Count : 0);
}

/// <summary>
/// What the model is told about a referenced artifact.
///
/// The source rides along only when the model cannot already see it: an older
/// version (the history holds several and the model would have to guess which),
/// a fence that never closed, or one far enough back that compaction may have
/// taken it. Otherwise naming it is enough — re-sending a 30 KB page to a model
/// that wrote it two turns ago doubles the bill for nothing.
/// </summary>
public static class ArtifactReferencePrompt
{
    private const int RecentAnswers = 3;
    private const int MaxInlineChars = 200_000;

    public static string Build(
        IReadOnlyList<ArtifactReferenceViewModel> references,
        IReadOnlyList<MessageViewModel> messages,
        MessageViewModel? current)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<交付物引用>");
        foreach (var reference in references)
        {
            if (reference.Item.Kind == ArtifactItemKind.Physical)
                AppendFile(sb, reference.Item);
            else
                AppendFence(sb, reference, messages, current);
        }

        sb.Append("</交付物引用>");
        return sb.ToString();
    }

    public static string HistoryNote(IReadOnlyList<ArtifactRefChip> refs) =>
        "（这条消息针对交付物"
        + string.Join("、", refs.Select(r => r.VersionCount > 1 ? $"「{r.Title}」第 {r.Version} 版" : $"「{r.Title}」"))
        + "）";

    private static void AppendFence(
        StringBuilder sb,
        ArtifactReferenceViewModel reference,
        IReadOnlyList<MessageViewModel> messages,
        MessageViewModel? current)
    {
        var item = reference.Item;
        var index = Math.Clamp(reference.VersionIndex, 0, item.Versions.Count - 1);
        var version = item.Versions[index];
        var isLatest = index == item.Versions.Count - 1;
        var language = FenceLanguage(version.Language, item.RenderKind);

        var which = item.Versions.Count > 1
            ? $"第 {index + 1} 版（共 {item.Versions.Count} 版，{(isLatest ? "即最新一版" : "不是最新一版")}）"
            : string.Empty;
        sb.AppendLine($"用户这条消息针对的是交付物「{item.Title}」{which}。");

        if (isLatest && version.IsComplete && IsRecent(version.Message, messages, current)
            && version.Content.Length <= MaxInlineChars)
        {
            var firstLine = FirstLine(version.Content);
            sb.AppendLine(firstLine is null
                ? $"它就是你在前面回复里输出的那个 ```{language} 代码块。"
                : $"它就是你在前面回复里输出的那个 ```{language} 代码块（首行 {firstLine}）。");
        }
        else
        {
            sb.AppendLine("这一版的完整源码：");
            sb.Append("```").AppendLine(language);
            sb.AppendLine(version.Content.TrimEnd());
            sb.AppendLine("```");
        }

        sb.AppendLine("请在这一版的基础上按用户的要求修改，没提到的部分保持原样；输出修改后的完整文件，放在一个代码块里，不要只给片段或差异。");
        sb.AppendLine(item.DeclaredName is { } name
            ? $"首行保留 {Comment(item.RenderKind, name)}，这样它会作为同一个交付物的新版本出现。"
            : $"首行加上文件名注释，例如 {Comment(item.RenderKind, ExampleName(item.RenderKind))}，以后的修改沿用这个文件名。");
    }

    private static void AppendFile(StringBuilder sb, ArtifactItemViewModel item)
    {
        sb.AppendLine($"用户这条消息针对的是工作区文件 {item.RelativePath}。");
        string? content = null;
        try
        {
            if (item.FullPath is { } path && File.Exists(path) && new FileInfo(path).Length <= MaxInlineChars)
                content = File.ReadAllText(path);
        }
        catch
        {
        }

        if (content is not null)
        {
            sb.Append("```").AppendLine(item.Language ?? string.Empty);
            sb.AppendLine(content.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine("请按用户的要求修改。能直接写文件时写回原文件；否则输出修改后的完整文件。");
        }
        else
        {
            sb.AppendLine($"文件较大，请用工具读取：{item.FullPath}");
        }
    }

    /// <summary>The answer is one of the last few before this turn — certainly
    /// still in context, whatever compaction has done to older ones.</summary>
    private static bool IsRecent(MessageViewModel answer, IReadOnlyList<MessageViewModel> messages, MessageViewModel? current)
    {
        var end = current is null ? messages.Count : IndexOf(messages, current);
        if (end < 0) end = messages.Count;
        var seen = 0;
        for (var i = end - 1; i >= 0 && seen < RecentAnswers; i--)
        {
            if (!string.Equals(messages[i].Role, ChatMessage.RoleAssistant, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReferenceEquals(messages[i], answer)) return true;
            seen++;
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<MessageViewModel> messages, MessageViewModel message)
    {
        for (var i = 0; i < messages.Count; i++)
            if (ReferenceEquals(messages[i], message)) return i;
        return -1;
    }

    private static string? FirstLine(string content)
    {
        foreach (var line in content.Split('\n', 3))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length <= 80 ? trimmed : null;
        }

        return null;
    }

    private static string FenceLanguage(string written, ArtifactRenderKind kind)
    {
        var normalized = FenceArtifactCapture.NormalizeLanguage(written);
        if (normalized.Length > 0 && normalized is not ("text" or "txt" or "plain" or "plaintext" or "xml")) return normalized;
        return kind switch
        {
            ArtifactRenderKind.Html => "html",
            ArtifactRenderKind.Svg => "svg",
            ArtifactRenderKind.Mermaid => "mermaid",
            ArtifactRenderKind.Table => "csv",
            _ => normalized,
        };
    }

    private static string Comment(ArtifactRenderKind kind, string name) => kind switch
    {
        ArtifactRenderKind.Mermaid => $"%% {name}",
        ArtifactRenderKind.Table => $"# {name}",
        _ => $"<!-- {name} -->",
    };

    private static string ExampleName(ArtifactRenderKind kind) => kind switch
    {
        ArtifactRenderKind.Svg => "diagram.svg",
        ArtifactRenderKind.Mermaid => "flow.mmd",
        ArtifactRenderKind.Table => "data.csv",
        _ => "page.html",
    };
}
