using MolaGPT.Core.Personalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MolaGPT.Core.Memory;

public sealed record MemoryTopic(string Id, string Group, string Title, string Summary, DateOnly? UpdatedAt = null);

public static class MemoryTopics
{
    public static readonly string[] Groups = ["关于你", "兴趣与话题", "项目与领域"];

    public static readonly MemoryTopic[] Defaults =
    [
        new("profile", Groups[0], "个人背景", "身份、生活背景与个人资料"),
        new("preferences", Groups[0], "交流偏好", "语言、表达习惯与协作方式"),
        new("projects", Groups[2], "进行中的项目", "正在推进的工作与长期目标"),
        new("recent", Groups[1], "近期事项", "近期有用的背景与安排")
    ];

    public static string DefaultId(MemorySection section) => section switch
    {
        MemorySection.Identity => "profile",
        MemorySection.Preference or MemorySection.Prohibition => "preferences",
        MemorySection.Project => "projects",
        _ => "recent"
    };

    public static string EntryTopicId(MemoryEntry entry) => entry.TopicId ?? DefaultId(entry.Section);

    public static MemorySection DefaultSection(string group) => group == Groups[2]
        ? MemorySection.Project : group == Groups[1] ? MemorySection.Context : MemorySection.Identity;
}

public sealed record MemoryTopicAssignment(string Title, string Group, string Summary, IReadOnlyList<string> EntryIds);

public static class MemoryTopicParser
{
    public static IReadOnlyList<MemoryTopicAssignment>? Parse(string reply)
    {
        var match = Regex.Match(reply, @"<topics>[\s\S]*?</topics>");
        if (!match.Success) return null;
        XElement root;
        try { root = XElement.Parse(match.Value); }
        catch (System.Xml.XmlException) { return null; }
        var result = new List<MemoryTopicAssignment>();
        foreach (var node in root.Elements("topic"))
        {
            var title = node.Attribute("title")?.Value.Trim();
            var group = node.Attribute("group")?.Value;
            var summary = node.Element("summary")?.Value.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(title) || title.Length > 60 || title.Contains('\n')
                || !MemoryTopics.Groups.Contains(group) || summary.Length > 240) return null;
            result.Add(new(title, group!, summary, node.Elements("entry")
                .Select(entry => entry.Attribute("id")?.Value).OfType<string>().ToArray()));
        }
        return result;
    }
}
