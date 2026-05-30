using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Tools.Vision;

public sealed class VisionProxyTool
{
    public const string ToolName = "view_image";
    private const int MaxVisionAnswerChineseChars = 300;

    private readonly ProviderRegistry _registry;
    private readonly Func<HttpClient> _httpFactory;

    public VisionProxyTool(ProviderRegistry registry, Func<HttpClient> httpFactory)
    {
        _registry = registry;
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
                + "matching the [图片#N] markers shown inline in the messages.",
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
        var prompt = string.IsNullOrWhiteSpace(query)
            ? $"请识别图片内容，并用不超过 {MaxVisionAnswerChineseChars} 个中文字符回答。"
            : $"请回答这个图片问题：{query!.Trim()}\n\n要求：只基于图片内容作答，不超过 {MaxVisionAnswerChineseChars} 个中文字符。";

        try
        {
            var provider = ResolveProvider(options);
            if (provider is null)
                return Error("No usable vision backend is configured.");

            var resolvedProvider = provider.Value;
            var request = new ChatRequest(
                ModelId: resolvedProvider.Model.Id,
                Messages:
                [
                    new ChatMessage(ChatMessage.RoleSystem, $"你是一个快速图片识别工具。只基于图片内容回答，答案必须简短，不超过 {MaxVisionAnswerChineseChars} 个中文字符。不要展开推理，不要补充无关背景。"),
                    new ChatMessage(ChatMessage.RoleUser, prompt, Attachments: [image])
                ],
                UseThinking: false,
                ThinkingParamKind: ResolveThinkingKind(resolvedProvider.Model));

            var text = await CollectAsync(resolvedProvider.Provider, request, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                source = "vision_proxy",
                image_index = index + 1,
                result = string.IsNullOrWhiteSpace(text) ? "(empty vision response)" : text
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    private (IChatProvider Provider, ProviderModel Model)? ResolveProvider(VisionProxyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ModelId))
            return null;

        var resolved = _registry.FindModel(options.ProviderId!, options.ModelId!);
        return resolved is null ? null : (resolved.Value.Provider, resolved.Value.Model);
    }

    private static ThinkingParamKind? ResolveThinkingKind(ProviderModel model)
    {
        var kind = model.ThinkingConfig?.Kind ?? ThinkingParamKindInference.InferFromModelId(model.Id);
        return kind == ThinkingParamKind.None ? null : kind;
    }

    private static async Task<string> CollectAsync(IChatProvider provider, ChatRequest request, CancellationToken ct)
    {
        var parts = new List<string>();
        await foreach (var chunk in provider.StreamChatAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaText))
                parts.Add(chunk.DeltaText);
            if (chunk.FinishReason is not null)
                break;
        }
        return string.Concat(parts).Trim();
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
