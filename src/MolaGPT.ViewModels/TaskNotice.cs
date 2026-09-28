using System.Text.RegularExpressions;
using System.Net;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.ViewModels;

/// <summary>
/// A background task's notification, read back out of the user-role message that
/// carried it to the model (<see cref="TaskTools.BuildNotification"/>). The
/// transcript shows it as a notice line rather than as something the user typed.
/// </summary>
public sealed partial record TaskNotice(string Id, string Status, string Label, string Elapsed, int Count,
    string? AgentMessage = null, string? TaskAgentId = null)
{
    [GeneratedRegex(@"<task-id>(?<v>[^<]*)</task-id>")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"<status>(?<v>[^<]*)</status>")]
    private static partial Regex StatusRegex();

    [GeneratedRegex(@"<label>(?<v>[^<]*)</label>")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"<elapsed>(?<v>[^<]*)</elapsed>")]
    private static partial Regex ElapsedRegex();

    [GeneratedRegex(@"<text>(?<v>[\s\S]*?)</text>")]
    private static partial Regex TextRegex();

    [GeneratedRegex(@"<agent-id>(?<v>[^<]*)</agent-id>")]
    private static partial Regex AgentIdRegex();

    public static TaskNotice? TryParse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        if (content.TrimStart().StartsWith("<agent-message>", StringComparison.Ordinal))
            return new TaskNotice(string.Empty, string.Empty,
                WebUtility.HtmlDecode(Read(AgentIdRegex(), content)), string.Empty,
                Regex.Matches(content, "<agent-message>").Count,
                WebUtility.HtmlDecode(Read(TextRegex(), content)));
        if (!content.TrimStart().StartsWith(TaskTools.NotificationOpenTag, StringComparison.Ordinal))
            return null;

        // Several tasks ending together arrive as one message; the first names the
        // line, the count says there were more.
        var count = Regex.Matches(content, Regex.Escape(TaskTools.NotificationOpenTag)).Count;
        var end = content.IndexOf("</task-notification>", StringComparison.Ordinal);
        var first = end < 0 ? content : content[..(end + "</task-notification>".Length)];
        return new TaskNotice(
            Read(IdRegex(), first),
            Read(StatusRegex(), first),
            Read(LabelRegex(), first),
            Read(ElapsedRegex(), first),
            count,
            TaskAgentId: Read(AgentIdRegex(), first));
    }

    private static string Read(Regex regex, string content) =>
        regex.Match(content) is { Success: true } match ? match.Groups["v"].Value.Trim() : string.Empty;

    public bool IsFailure => Status is "failed" or "interrupted";

    public string StatusText => Status switch
    {
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已停止",
        "interrupted" => "已中断",
        _ => "已结束"
    };

    /// <summary>「后台任务已完成 · 清洗地震目录 · 6m12s」</summary>
    public string Text
    {
        get
        {
            if (AgentMessage is not null)
                return Count > 1 ? $"{Count} 条子 Agent 消息"
                    : string.IsNullOrWhiteSpace(AgentMessage)
                        ? "子 Agent 发来消息"
                        : $"{Label} · {AgentMessage.Replace('\n', ' ').Trim()}";
            var head = Count > 1 ? $"{Count} 个后台任务已结束"
                : string.IsNullOrWhiteSpace(TaskAgentId) ? $"后台任务{StatusText}" : $"子 Agent{StatusText}";
            var parts = new List<string> { head };
            if (Count <= 1 && !string.IsNullOrWhiteSpace(Label)) parts.Add(Label);
            if (Count <= 1 && !string.IsNullOrWhiteSpace(Elapsed)) parts.Add(Elapsed);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Warning for a task that failed or was cut off, a check otherwise.</summary>
    public string Glyph => IsFailure ? "\uE7BA" : "\uE73E";
}
