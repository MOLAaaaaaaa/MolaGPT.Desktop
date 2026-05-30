using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.Tools.Mcp;

public static partial class McpToolName
{
    public const string Prefix = "mcp__";

    public static string Build(string serverId, string toolName) =>
        $"{Prefix}{Slugify(serverId)}__{Slugify(toolName)}";

    public static bool TryDecode(string value, out string serverSlug, out string toolSlug)
    {
        serverSlug = string.Empty;
        toolSlug = string.Empty;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = value[Prefix.Length..];
        var split = rest.IndexOf("__", StringComparison.Ordinal);
        if (split <= 0 || split >= rest.Length - 2) return false;
        serverSlug = rest[..split];
        toolSlug = rest[(split + 2)..];
        return true;
    }

    public static string Slugify(string value)
    {
        var text = InvalidToolNameChars().Replace(value.Trim(), "_").Trim('_');
        if (string.IsNullOrWhiteSpace(text)) text = "tool";
        if (char.IsDigit(text[0])) text = "_" + text;
        return text.Length <= 48 ? text : text[..48];
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]+")]
    private static partial Regex InvalidToolNameChars();
}
