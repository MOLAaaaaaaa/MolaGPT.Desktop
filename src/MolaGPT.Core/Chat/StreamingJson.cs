namespace MolaGPT.Core.Chat;

/// <summary>Skip JSON parsing while a streamed object or array is visibly unfinished.</summary>
public static class StreamingJson
{
    public static bool CouldBeComplete(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.AsSpan().Trim();
        if (text[0] != '{' && text[0] != '[') return true;
        if (text[0] == '{' && text[^1] != '}' || text[0] == '[' && text[^1] != ']')
            return false;

        var depth = 0;
        var quoted = false;
        var escaped = false;
        foreach (var c in text)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
        }
        return !quoted && depth == 0;
    }
}
