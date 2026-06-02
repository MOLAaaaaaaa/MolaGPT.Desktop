using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Chat.Tools.ImageGeneration;

public sealed class ImageGenerationTool
{
    public const string ToolName = "generate_image";

    private readonly Func<HttpClient> _httpFactory;
    private readonly Func<byte[], string?, string?, string?>? _saveAttachment;

    public ImageGenerationTool(
        Func<HttpClient> httpFactory,
        Func<byte[], string?, string?, string?>? saveAttachment = null)
    {
        _httpFactory = httpFactory;
        _saveAttachment = saveAttachment;
    }

    public static object BuildOpenAiToolDefinition() => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description = "Generate an image from a text prompt using the configured image generation API.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    prompt = new
                    {
                        type = "string",
                        description = "The text description of the image to generate."
                    }
                },
                required = new[] { "prompt" }
            }
        }
    };

    public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(
        ImageGenerationOptions options,
        string prompt,
        CancellationToken ct)
    {
        Validate(options, prompt);

        if (ImageApiFormat.IsChatImage(options.Format))
            return await GenerateViaChatAsync(options, prompt, inputImage: null, inputMime: null, ct).ConfigureAwait(false);

        var endpoint = NetworkSecurity.CombineEndpoint(
            options.BaseUrl!, DefaultPath(options.GenerationPath, "v1/images/generations"), "图像服务接入地址");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(BuildRequestBody(options, prompt))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像生成", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return await ParseImagesAsync(http, doc.RootElement, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GeneratedImage>> EditAsync(
        ImageGenerationOptions options,
        string prompt,
        byte[] imageBytes,
        string? imageMime,
        CancellationToken ct)
    {
        Validate(options, prompt);
        if (!options.SupportsEdit)
            throw new InvalidOperationException("当前模型不支持图像编辑。");
        if (imageBytes.Length == 0)
            throw new InvalidOperationException("没有可编辑的图片。");

        // Chat-completions dialects (OpenRouter / nano-banana) edit through the
        // same endpoint as generation, with the source image carried inline.
        if (ImageApiFormat.IsChatImage(options.Format))
            return await GenerateViaChatAsync(options, prompt, imageBytes, imageMime, ct).ConfigureAwait(false);

        var endpoint = NetworkSecurity.CombineEndpoint(
            options.BaseUrl!, DefaultPath(options.EditPath, "v1/images/edits"), "图像服务接入地址");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(options.Model!.Trim()), "model");
        content.Add(new StringContent(prompt.Trim()), "prompt");
        content.Add(new StringContent("1"), "n");
        if (!string.IsNullOrWhiteSpace(options.Size) && !string.Equals(options.Size, "auto", StringComparison.OrdinalIgnoreCase))
            content.Add(new StringContent(options.Size.Trim()), "size");

        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(imageMime) ? "image/png" : imageMime);
        content.Add(imageContent, "image", $"source{ExtensionFor(imageMime)}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像编辑", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return await ParseImagesAsync(http, doc.RootElement, ct).ConfigureAwait(false);
    }

    public async Task<string> ExecuteToolAsync(
        string argumentsJson,
        ImageGenerationOptions? options,
        CancellationToken ct)
    {
        if (options?.Enabled != true)
            return Error("Image generation is not enabled.");
        if (_saveAttachment is null)
            return Error("Image attachment storage is not available.");

        var prompt = ParsePrompt(argumentsJson);
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("A non-empty prompt is required.");

        try
        {
            var images = await GenerateAsync(options, prompt!, ct).ConfigureAwait(false);
            var saved = images.Select((image, index) =>
            {
                var fileName = $"generated-image-{index + 1}{ExtensionFor(image.MimeType)}";
                var localName = _saveAttachment(image.Bytes, image.MimeType, fileName);
                return new
                {
                    local_name = localName,
                    file_name = fileName,
                    mime_type = image.MimeType,
                    revised_prompt = image.RevisedPrompt
                };
            }).ToArray();

            return JsonSerializer.Serialize(new
            {
                success = saved.Length > 0,
                source = "image_generation",
                image_path = saved.FirstOrDefault()?.local_name,
                revised_prompt = images.FirstOrDefault()?.RevisedPrompt,
                images = saved
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(ImageGenerationOptions options, string prompt)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model!.Trim(),
            ["prompt"] = prompt.Trim(),
            ["n"] = 1
        };

        if (!string.IsNullOrWhiteSpace(options.Size))
            body["size"] = options.Size.Trim();

        // GPT Image models return b64_json by default. DALL-E still needs the
        // explicit flag if we want local bytes instead of a temporary URL.
        if (IsDallE(options.Model))
            body["response_format"] = "b64_json";

        if (IsDallE3(options.Model) && !string.IsNullOrWhiteSpace(options.Style))
            body["style"] = options.Style.Trim();

        return body;
    }

    private static async Task<IReadOnlyList<GeneratedImage>> ParseImagesAsync(
        HttpClient http,
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<GeneratedImage>();

        var rootMime = MimeFromOutputFormat(ReadString(root, "output_format"));
        var images = new List<GeneratedImage>();
        foreach (var item in data.EnumerateArray())
        {
            var revisedPrompt = ReadString(item, "revised_prompt");
            if (ReadString(item, "b64_json") is { Length: > 0 } b64)
            {
                images.Add(new GeneratedImage(Convert.FromBase64String(b64), rootMime, revisedPrompt));
                continue;
            }

            if (ReadString(item, "url") is not { Length: > 0 } url)
                continue;

            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像下载", ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mime = response.Content.Headers.ContentType?.MediaType ?? rootMime;
            images.Add(new GeneratedImage(bytes, mime, revisedPrompt));
        }

        return images;
    }

    // ---- openai-chat-image dialect (OpenRouter / nano-banana) ------------
    // Image generation and editing both run through /chat/completions with
    // modalities:["image","text"]. The result image comes back as a base64
    // data URL at choices[].message.images[].image_url.url.
    private async Task<IReadOnlyList<GeneratedImage>> GenerateViaChatAsync(
        ImageGenerationOptions options,
        string prompt,
        byte[]? inputImage,
        string? inputMime,
        CancellationToken ct)
    {
        var endpoint = NetworkSecurity.CombineEndpoint(
            options.BaseUrl!, DefaultPath(options.GenerationPath, "v1/chat/completions"), "图像服务接入地址");

        object messageContent;
        if (inputImage is { Length: > 0 })
        {
            var mime = string.IsNullOrWhiteSpace(inputMime) ? "image/png" : inputMime;
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(inputImage)}";
            messageContent = new object[]
            {
                new { type = "image_url", image_url = new { url = dataUrl } },
                new { type = "text", text = prompt.Trim() }
            };
        }
        else
        {
            messageContent = prompt.Trim();
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model!.Trim(),
            ["messages"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "user", ["content"] = messageContent }
            },
            ["modalities"] = new[] { "image", "text" },
            ["stream"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像生成", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseChatImages(doc.RootElement);
    }

    private static IReadOnlyList<GeneratedImage> ParseChatImages(JsonElement root)
    {
        var images = new List<GeneratedImage>();
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;
            if (!message.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var img in imgs.EnumerateArray())
            {
                var url = img.ValueKind == JsonValueKind.Object
                          && img.TryGetProperty("image_url", out var imageUrl)
                          && imageUrl.ValueKind == JsonValueKind.Object
                          && imageUrl.TryGetProperty("url", out var urlProp)
                          && urlProp.ValueKind == JsonValueKind.String
                    ? urlProp.GetString()
                    : null;
                if (TryDecodeDataUrl(url, out var bytes, out var mime))
                    images.Add(new GeneratedImage(bytes, mime, RevisedPrompt: null));
            }
        }

        return images;
    }

    private static bool TryDecodeDataUrl(string? url, out byte[] bytes, out string mimeType)
    {
        bytes = Array.Empty<byte>();
        mimeType = "image/png";
        if (string.IsNullOrWhiteSpace(url)) return false;

        var payload = url;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return false;
            var meta = url.Substring(5, comma - 5);     // between "data:" and ","
            payload = url[(comma + 1)..];
            var semi = meta.IndexOf(';');
            var mime = semi >= 0 ? meta[..semi] : meta;
            if (!string.IsNullOrWhiteSpace(mime)) mimeType = mime.Trim();
        }

        try
        {
            bytes = Convert.FromBase64String(payload.Trim());
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DefaultPath(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static void Validate(ImageGenerationOptions options, string prompt)
    {
        if (options.Enabled != true)
            throw new InvalidOperationException("图像生成尚未启用。");
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new InvalidOperationException("请填写图像服务的接入地址。");
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("请填写图像服务的 API Key。");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("请选择图像模型。");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("请输入图像描述。");
    }

    private static string? ParsePrompt(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;
        using var doc = JsonDocument.Parse(argumentsJson);
        return ReadString(doc.RootElement, "prompt");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool IsDallE(string? model) =>
        model?.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDallE3(string? model) =>
        string.Equals(model, "dall-e-3", StringComparison.OrdinalIgnoreCase);

    private static string MimeFromOutputFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };

    private static string ExtensionFor(string? mimeType) =>
        mimeType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png"
        };

    private static string Error(string message) => JsonSerializer.Serialize(new
    {
        success = false,
        error = message
    });
}

public sealed record GeneratedImage(byte[] Bytes, string MimeType, string? RevisedPrompt);
