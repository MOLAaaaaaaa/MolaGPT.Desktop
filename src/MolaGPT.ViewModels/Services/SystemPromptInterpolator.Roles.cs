using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.ViewModels.Services;

public static partial class SystemPromptInterpolator
{
    private static string ExpandRole(string template, PromptVariables vars, HashSet<string> resolving, int nesting = 0)
    {
        if (nesting > 64) throw new InvalidDataException("角色变量嵌套过深。");
        var output = new StringBuilder();
        for (var cursor = 0; cursor < template.Length;)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0) { output.Append(template.AsSpan(cursor)); break; }
            output.Append(template.AsSpan(cursor, start - cursor));
            var end = start + 2;
            var depth = 1;
            for (; end + 1 < template.Length; end++)
            {
                if (template[end] == '{' && template[end + 1] == '{') { depth++; end++; }
                else if (template[end] == '}' && template[end + 1] == '}')
                {
                    if (--depth == 0) break;
                    end++;
                }
            }
            if (depth != 0) { output.Append(template.AsSpan(start)); break; }
            var raw = template[(start + 2)..end];
            var body = raw.Trim();
            cursor = end + 2;
            if (body.Equals("trim", StringComparison.OrdinalIgnoreCase))
            {
                while (output.Length > 0 && output[^1] is '\r' or '\n') output.Length--;
                while (cursor < template.Length && template[cursor] is '\r' or '\n') cursor++;
                continue;
            }
            if (body.StartsWith("//", StringComparison.Ordinal) || body.StartsWith("comment:", StringComparison.OrdinalIgnoreCase))
                continue;
            var colon = body.IndexOf(':');
            if (colon >= 0)
            {
                var function = body[..colon].Trim().ToLowerInvariant();
                var arguments = body[(colon + 1)..].TrimStart(':');
                if (function == "hidden_key")
                {
                    if (vars.KeepHiddenKeys) output.Append(ExpandRole(arguments, vars, resolving, nesting + 1));
                    continue;
                }
                if (function == "outlet" && vars.Outlets is not null)
                {
                    output.Append(vars.Outlets.GetValueOrDefault(arguments.Trim(), ""));
                    continue;
                }
                if (function is "random" or "pick")
                {
                    var choices = SplitArguments(arguments, "::");
                    if (choices.Count == 1) choices = SplitArguments(arguments, ",");
                    var seed = function == "pick" ? vars.StableSeed : vars.GenerationSeed;
                    var index = (int)(RoleDraw(seed, template + ":" + start) * choices.Count);
                    output.Append(ExpandRole(choices[index], vars, resolving, nesting + 1));
                    continue;
                }
                if (function == "roll")
                {
                    var dice = ExpandRole(arguments, vars, resolving, nesting + 1).Trim();
                    var match = DiceRegex().Match(dice);
                    if (!match.Success) throw new InvalidDataException("骰子格式无效。");
                    var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;
                    var sides = int.Parse(match.Groups["sides"].Value, CultureInfo.InvariantCulture);
                    if (count is < 1 or > 100 || sides < 1) throw new InvalidDataException("骰子数量或面数无效。");
                    long total = match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
                    for (var i = 0; i < count; i++) total += 1 + (long)(RoleDraw(vars.GenerationSeed, template + ":" + start + ":" + i) * sides);
                    output.Append(total.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
            }
            var name = body.ToLowerInvariant();
            string? field = null;
            var hasField = name == "original" || vars.RoleFields!.TryGetValue(name, out field);
            if (hasField)
            {
                if (!resolving.Add(name)) throw new InvalidDataException("角色变量存在循环引用：" + name);
                output.Append(ExpandRole(name == "original" ? vars.Original ?? "" : field ?? "", vars, resolving, nesting + 1));
                resolving.Remove(name);
            }
            else output.Append(Interpolate("{{" + raw + "}}", vars with { RoleFields = null }));
        }
        return output.ToString();
    }

    private static double RoleDraw(string? seed, string location)
    {
        if (string.IsNullOrEmpty(seed)) throw new InvalidOperationException("角色变量缺少随机种子。");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + location));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) / 4294967296d;
    }

    private static List<string> SplitArguments(string input, string separator)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 1 < input.Length &&
                (input[i + 1] == separator[0] || separator == "," && input[i + 1] == '\\'))
            { current.Append(input[++i]); continue; }
            if (i + 1 < input.Length && input[i] == '{' && input[i + 1] == '{')
            { depth++; current.Append("{{"); i++; continue; }
            if (i + 1 < input.Length && input[i] == '}' && input[i + 1] == '}')
            { depth--; current.Append("}}"); i++; continue; }
            if (depth == 0 && input.AsSpan(i).StartsWith(separator, StringComparison.Ordinal))
            { values.Add(current.ToString().Trim()); current.Clear(); i += separator.Length - 1; }
            else current.Append(input[i]);
        }
        values.Add(current.ToString().Trim());
        return values;
    }

    [GeneratedRegex(@"^(?:(?<count>\d+)?d)?(?<sides>\d+)(?<offset>[+-]\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiceRegex();
}
