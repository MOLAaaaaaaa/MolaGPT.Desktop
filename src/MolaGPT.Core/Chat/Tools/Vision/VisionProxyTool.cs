using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Tools.Vision;

public sealed class VisionProxyTool
{
    public const string ToolName = "view_image";
    private const int MaxVisionAnswerChineseChars = 300;

    private readonly VisionBackend _backend;
    private readonly Func<HttpClient> _httpFactory;

    public VisionProxyTool(ProviderRegistry registry, Func<HttpClient> httpFactory)
    {
        _backend = new VisionBackend(registry);
        _httpFactory = httpFactory;
    }

    public static object BuildOpenAiToolDefinition() => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description = "Inspect a user-attached image through a configured vision model. "
                + "Images are numbered globally across the whole conversation in upload order, "
                + "matching the [图片#N] markers shown inline in the messages. "
                + "For an image file sitting in the working directory rather than attached to a "
                + "message, use analyze_image instead.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    image_index = new
                    {
                        type = "integer",
                        description = "1-based global index of the image, matching the [图片#N] marker. Defaults to the most recent image."
                    },
                    query = new
                    {
                        type = "string",
                        description = "What to inspect or answer about the image."
                    }
                }
            }
        }
    };

    public async Task<string> ExecuteAsync(
        string argumentsJson,
        ChatToolContext context,
        VisionProxyOptions? options,
        CancellationToken ct)
    {
        if (options?.Enabled != true)
            return Error("Vision proxy is not enabled.");

        var images = context.Request.Messages
            .Where(m => m.Role == ChatMessage.RoleUser)
            .SelectMany(m => m.Attachments ?? Array.Empty<Attachment>())
            .Where(a => a.Kind == AttachmentKind.Image)
            .ToList();
        if (images.Count == 0)
            return Error("No user image attachment is available.");

        var (index, query) = ParseArguments(argumentsJson, images.Count);
        var image = images[index];
        // Unavailable images stay in the list so every picture keeps the ordinal
        // the prompt showed. Selecting one has to fail loudly rather than ship
        // zero bytes to the vision model.
        if (image.IsUnavailable)
            return Error($"图片#{index + 1}（{image.DisplayName}）不可用：{image.UnavailableReason}");
        var prompt = string.IsNullOrWhiteSpace(query)
            ? $"请识别图片内容，并用不超过 {MaxVisionAnswerChineseChars} 个中文字符回答。"
            : $"请回答这个图片问题：{query!.Trim()}\n\n要求：只基于图片内容作答，不超过 {MaxVisionAnswerChineseChars} 个中文字符。";

        var answer = await _backend.AskAsync(
            options,
            image,
            $"你是一个快速图片识别工具。只基于图片内容回答，答案必须简短，不超过 {MaxVisionAnswerChineseChars} 个中文字符。不要展开推理，不要补充无关背景。",
            prompt,
            ct).ConfigureAwait(false);

        if (answer.Error is not null)
            return Error(answer.Error);

        return JsonSerializer.Serialize(new
        {
            success = true,
            source = "vision_proxy",
            image_index = index + 1,
            result = string.IsNullOrWhiteSpace(answer.Text) ? "(empty vision response)" : answer.Text
        });
    }

    private static (int Index, string? Query) ParseArguments(string argumentsJson, int imageCount)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return (imageCount - 1, null);
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var requested = root.TryGetProperty("image_index", out var node)
                        && node.ValueKind == JsonValueKind.Number
                        && node.TryGetInt32(out var idx)
            ? idx
            : imageCount;
        var index = Math.Clamp(requested, 1, imageCount) - 1;
        var query = root.TryGetProperty("query", out var queryNode)
                    && queryNode.ValueKind == JsonValueKind.String
            ? queryNode.GetString()
            : null;
        return (index, query);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new
    {
        success = false,
        error = message
    });
}
