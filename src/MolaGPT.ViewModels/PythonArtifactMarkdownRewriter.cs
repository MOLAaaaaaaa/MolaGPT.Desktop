using System.Text.Json;
using System.Text.RegularExpressions;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tools.PythonExecution;

namespace MolaGPT.ViewModels;

internal static partial class PythonArtifactMarkdownRewriter
{
    public static ArtifactContext? CreateContext(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        var byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workingDirectories = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            AddWorkingDirectory(root, workingDirectories);
            if (root.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    if (artifact.ValueKind != JsonValueKind.Object) continue;
                    var path = ReadString(artifact, "path");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                    AddPath(byRelativePath, ReadString(artifact, "relative_path"), path!);
                    AddPath(byRelativePath, ReadString(artifact, "name"), path!);
                }
            }
        }
        catch (JsonException)
        {
            AddRegexWorkingDirectory(resultJson, workingDirectories);
        }

        return byRelativePath.Count == 0 && workingDirectories.Count == 0
            ? null
            : new ArtifactContext(byRelativePath, workingDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static IReadOnlyList<ArtifactContext> CreateContexts(IEnumerable<ToolCallDelta>? toolCalls)
    {
        if (toolCalls is null)
            return Array.Empty<ArtifactContext>();

        var contexts = new List<ArtifactContext>();
        foreach (var toolCall in toolCalls)
        {
            if (!string.Equals(toolCall.Name, PythonExecutionTool.ToolName, StringComparison.Ordinal))
                continue;

            var context = CreateContext(toolCall.ResultPreviewJson);
            if (context is not null)
                contexts.Add(context);
        }

        return contexts;
    }

    /// <summary>Build a rewrite context from image attachment chips (BYOK
    /// <c>generate_image</c>). Maps each chip's display file name (and its
    /// content-hash local name) to the real on-disk AttachmentStore path, so an
    /// inline <c>![](generated-image-1.png)</c> resolves to a loadable local file
    /// — fixing both display and click-to-zoom. Self-contained (resolves against
    /// the fixed store root) so it works on reload without a store instance.</summary>
    public static ArtifactContext? CreateAttachmentContext(IEnumerable<AttachmentChip>? attachments)
    {
        if (attachments is null) return null;

        string root;
        try { root = MolaGPT.Storage.AttachmentStore.DefaultRoot(); }
        catch { return null; }

        var byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chip in attachments)
        {
            var localName = chip.LocalName;
            // local names are bare content hashes; reject anything with separators.
            if (string.IsNullOrWhiteSpace(localName)
                || localName.Contains('/') || localName.Contains('\\')
                || localName.Contains("..", StringComparison.Ordinal))
                continue;

            var path = Path.Combine(root, localName);
            if (!File.Exists(path)) continue;

            if (!string.IsNullOrWhiteSpace(chip.FileName))
                byRelativePath[NormalizeRelativeKey(chip.FileName)] = path;
            byRelativePath[NormalizeRelativeKey(localName)] = path;
        }

        return byRelativePath.Count == 0
            ? null
            : new ArtifactContext(byRelativePath, Array.Empty<string>());
    }

    public static string Rewrite(string content, IReadOnlyList<ArtifactContext>? contexts)
    {
        if (string.IsNullOrWhiteSpace(content) || contexts is not { Count: > 0 })
            return content;

        return MarkdownImageRegex().Replace(content, match =>
        {
            var urlGroup = match.Groups["url"];
            if (!urlGroup.Success)
                return match.Value;

            var originalUrl = urlGroup.Value.Trim();
            if (string.IsNullOrWhiteSpace(originalUrl) || !IsRelativeMarkdownImageUrl(originalUrl))
                return match.Value;

            var resolved = ResolveRelativePath(originalUrl, contexts);
            if (string.IsNullOrWhiteSpace(resolved))
                return match.Value;

            var replacementUrl = new Uri(resolved!).AbsoluteUri;
            var relativeIndex = urlGroup.Index - match.Index;
            return match.Value[..relativeIndex] + replacementUrl + match.Value[(relativeIndex + urlGroup.Length)..];
        });
    }

    private static string? ResolveRelativePath(string url, IReadOnlyList<ArtifactContext> contexts)
    {
        var normalized = NormalizeRelativeKey(url);
        foreach (var context in contexts)
        {
            if (context.ByRelativePath.TryGetValue(normalized, out var explicitPath))
                return explicitPath;

            var fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName)
                && context.ByRelativePath.TryGetValue(fileName, out explicitPath))
            {
                return explicitPath;
            }

            foreach (var workingDirectory in context.WorkingDirectories)
            {
                var candidate = Path.GetFullPath(Path.Combine(workingDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(candidate) && IsPathInside(candidate, workingDirectory))
                    return candidate;
            }
        }

        return null;
    }

    private static bool IsRelativeMarkdownImageUrl(string url)
    {
        var value = url.Trim().Trim('"', '\'');
        if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
            return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return false;
        if (Path.IsPathRooted(value) || LooksLikeWindowsPath(value))
            return false;
        return true;
    }

    private static bool LooksLikeWindowsPath(string value) =>
        value.Length >= 3
        && char.IsLetter(value[0])
        && value[1] == ':'
        && (value[2] == '\\' || value[2] == '/');

    private static void AddWorkingDirectory(JsonElement root, List<string> workingDirectories)
    {
        var workingDirectory = ReadString(root, "working_directory");
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            workingDirectories.Add(Path.GetFullPath(workingDirectory!));
    }

    private static void AddRegexWorkingDirectory(string resultJson, List<string> workingDirectories)
    {
        var match = WorkingDirectoryRegex().Match(resultJson);
        if (!match.Success)
            return;

        var value = DecodeJsonString(match.Groups["value"].Value);
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            workingDirectories.Add(Path.GetFullPath(value!));
    }

    private static string? DecodeJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + value + "\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddPath(Dictionary<string, string> paths, string? key, string path)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var normalized = NormalizeRelativeKey(key);
        if (normalized.Length > 0)
            paths[normalized] = path;
    }

    private static string NormalizeRelativeKey(string value)
    {
        var normalized = SafeUnescape(value).Trim().Trim('"', '\'').Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"!\[(?:\\.|[^\]\\])*\]\((?<url>[^)\r\n]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"""working_directory""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""", RegexOptions.CultureInvariant)]
    private static partial Regex WorkingDirectoryRegex();

    internal sealed record ArtifactContext(
        IReadOnlyDictionary<string, string> ByRelativePath,
        IReadOnlyList<string> WorkingDirectories);
}
