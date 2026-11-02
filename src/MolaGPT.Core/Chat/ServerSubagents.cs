using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat;

public static partial class ServerSubagents
{
    [GeneratedRegex("""<blockquote\b(?=[^>]*\btool-subagent-blockquote\b)[^>]*data-subagent-batch="(?<batch>[^"]+)"[^>]*>[\s\S]*?</blockquote>""", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public static string? Batch(string source) => Placeholder().Match(source) is { Success: true } match
        ? match.Groups["batch"].Value : null;

    public static JsonObject Merge(JsonObject? batches, JsonObject frame)
    {
        var merged = (JsonObject?)batches?.DeepClone() ?? new JsonObject();
        var batch = frame["batch"]!.GetValue<string>();
        var agents = merged[batch] as JsonArray ?? new JsonArray();
        if (merged[batch] is null) merged[batch] = agents;
        foreach (var item in frame["agents"]!.AsArray().OfType<JsonObject>())
        {
            var id = Text(item, "id");
            var existing = agents.OfType<JsonObject>().FirstOrDefault(a => Text(a, "id") == id);
            if (existing is null) agents.Add(item.DeepClone());
            else foreach (var field in item) existing[field.Key] = field.Value?.DeepClone();
        }
        return merged;
    }

    public static JsonObject? Finish(JsonObject? batches)
    {
        if (batches is null) return null;
        var result = (JsonObject)batches.DeepClone();
        foreach (var agent in result.SelectMany(b => b.Value!.AsArray()).OfType<JsonObject>())
            if (Text(agent, "status") == "running")
            {
                agent["status"] = "stopped";
                agent["ended_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        return result;
    }

    public static string Text(JsonObject agent, string key) => agent[key]?.GetValue<string>() ?? string.Empty;

    public static string Status(JsonObject agent) => Text(agent, "status") switch
    {
        "running" => "运行中", "completed" => "已完成", "failed" => "失败",
        "timeout" => "已超时", "rejected" => "未派出", _ => "已停止"
    };

    public static string Report(JsonObject agent, int limit = int.MaxValue)
    {
        var report = Text(agent, "report");
        var text = new StringBuilder(report[..Math.Min(report.Length, limit)]);
        if (agent["sources"] is JsonArray sources)
            foreach (var source in sources.OfType<JsonObject>())
                text.Append("\n\n[").Append(Text(source, "title")).Append("](").Append(Text(source, "url")).Append(')');
        if (agent["files"] is JsonArray files)
            foreach (var file in files.OfType<JsonObject>())
                text.Append("\n\n[").Append(Text(file, "name")).Append("](").Append(Text(file, "url")).Append(')');
        return text.ToString();
    }

    public static string Expand(string content, JsonObject? batches) => Placeholder().Replace(content, match =>
    {
        if (batches?[match.Groups["batch"].Value] is not JsonArray agents) return string.Empty;
        return "[子代理任务记录]\n" + string.Join("\n\n", agents.OfType<JsonObject>().Select(agent =>
            $"{Text(agent, "label")}：{Status(agent)}\n{Report(agent, 4000)}\n{Text(agent, "error")}"));
    });
}
