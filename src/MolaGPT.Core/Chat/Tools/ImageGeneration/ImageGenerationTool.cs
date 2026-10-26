using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Chat.Tools.ImageGeneration;

public sealed class ImageGenerationTool
{
    public const string ToolName = "generate_image";

    /// <summary>Ceiling on one batch. Every extra picture is a paid request and
    /// a parallel connection, so this is a spend limit as much as a UI one.</summary>
    public const int MaxBatchSize = 4;

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

    /// <summary>Generation, <paramref name="count"/> pictures at a time. See
    /// <see cref="FanOutAsync"/> for why that is not one request.</summary>
    public Task<IReadOnlyList<GeneratedImage>> GenerateAsync(
        ImageGenerationOptions options,
        string prompt,
        int count,
        CancellationToken ct) =>
        FanOutAsync(token => GenerateAsync(options, prompt, token), count, ct);

    /// <summary>Editing, <paramref name="count"/> variants of the same source.</summary>
    public Task<IReadOnlyList<GeneratedImage>> EditAsync(
        ImageGenerationOptions options,
        string prompt,
        byte[] imageBytes,
        string? imageMime,
        int count,
        CancellationToken ct) =>
        FanOutAsync(token => EditAsync(options, prompt, imageBytes, imageMime, token), count, ct);

    public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(
        ImageGenerationOptions options,
        string prompt,
        CancellationToken ct)
    {
        Validate(options, prompt);

        var effectivePrompt = ImagePromptComposer.Compose(options, prompt, isEdit: false);

        if (options.ProviderType == "gemini")
            return await GenerateViaGeminiAsync(options, effectivePrompt, null, null, ct).ConfigureAwait(false);
        if (ImageApiFormat.IsChatImage(options.Format))
            return await GenerateViaChatAsync(options, effectivePrompt, inputImage: null, inputMime: null, ct).ConfigureAwait(false);

        return await GenerateViaImagesAsync(options, effectivePrompt, null, null, ct).ConfigureAwait(false);
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

        var effectivePrompt = ImagePromptComposer.Compose(options, prompt, isEdit: true);

        if (options.ProviderType == "gemini")
            return await GenerateViaGeminiAsync(options, effectivePrompt, imageBytes, imageMime, ct).ConfigureAwait(false);
        if (ImageApiFormat.IsChatImage(options.Format))
            return await GenerateViaChatAsync(options, effectivePrompt, imageBytes, imageMime, ct).ConfigureAwait(false);

        var dialect = ImagesDialectFor(options);
        if (dialect == ImagesDialect.Zhipu)
            throw new InvalidOperationException("当前图像接口不支持图像编辑。");
        if (dialect != ImagesDialect.OpenAi)
            return await GenerateViaImagesAsync(options, effectivePrompt, imageBytes, imageMime, ct).ConfigureAwait(false);

        var endpoint = NetworkSecurity.CombineEndpoint(
            options.BaseUrl!, DefaultPath(options.EditPath, "v1/images/edits"), "图像服务接入地址");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(options.Model!.Trim()), "model");
        content.Add(new StringContent(effectivePrompt), "prompt");
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
        ApplyHeaders(request, options);

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
        string? workspaceRoot,
        CancellationToken ct)
    {
        if (options?.Enabled != true)
            return Error("Image generation is not enabled.");
        if (_saveAttachment is null)
            return Error("Image attachment storage is not available.");
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Error("The conversation workspace is not available.");

        var prompt = ParsePrompt(argumentsJson);
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("A non-empty prompt is required.");

        try
        {
            var images = await GenerateAsync(options, prompt!, ct).ConfigureAwait(false);
            Directory.CreateDirectory(workspaceRoot);
            var saved = images.Select((image, index) =>
            {
                var displayName = $"generated-image-{index + 1}{ExtensionFor(image.MimeType)}";
                var localName = _saveAttachment(image.Bytes, image.MimeType, displayName)
                    ?? throw new InvalidOperationException("Generated image could not be saved.");
                var fileName = AvailableFileName(workspaceRoot, displayName);
                File.WriteAllBytes(Path.Combine(workspaceRoot, fileName), image.Bytes);
                return new
                {
                    local_name = localName,
                    image_path = fileName,
                    file_name = fileName,
                    mime_type = image.MimeType,
                    revised_prompt = image.RevisedPrompt
                };
            }).ToArray();

            return JsonSerializer.Serialize(new
            {
                success = saved.Length > 0,
                source = "image_generation",
                image_path = saved.FirstOrDefault()?.image_path,
                local_name = saved.FirstOrDefault()?.local_name,
                revised_prompt = images.FirstOrDefault()?.RevisedPrompt,
                images = saved
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    private static string AvailableFileName(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName))) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    /// <summary>
    /// N pictures from N requests, in parallel.
    ///
    /// Neither dialect has a batch field worth using: <c>images/generations</c>
    /// takes <c>n</c>, but DALL·E 3 rejects anything above 1 and the value is
    /// per-model rather than per-endpoint; the chat-image dialect has no such
    /// field at all. Asking four times is the one way that works everywhere, and
    /// it costs the same — these APIs bill per picture, not per request.
    ///
    /// Partial failure keeps what came back: three pictures and one timeout is a
    /// better answer than nothing. The failure is only rethrown when every
    /// request failed, so a wrong key or an unsupported model still surfaces.
    /// </summary>
    private static async Task<IReadOnlyList<GeneratedImage>> FanOutAsync(
        Func<CancellationToken, Task<IReadOnlyList<GeneratedImage>>> run,
        int count,
        CancellationToken ct)
    {
        var requests = Math.Clamp(count, 1, MaxBatchSize);
        if (requests == 1) return await run(ct).ConfigureAwait(false);

        var tasks = new Task<IReadOnlyList<GeneratedImage>>[requests];
        for (var i = 0; i < requests; i++) tasks[i] = run(ct);

        // WhenAll surfaces only the first failure; every task is inspected below.
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* handled per task */ }

        var images = new List<GeneratedImage>();
        Exception? failure = null;
        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully) images.AddRange(task.Result);
            else failure ??= task.Exception?.InnerException ?? task.Exception;
        }

        if (images.Count == 0 && failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return images;
    }

    private async Task<IReadOnlyList<GeneratedImage>> GenerateViaImagesAsync(
        ImageGenerationOptions options, string prompt, byte[]? inputImage, string? inputMime, CancellationToken ct)
    {
        var editing = inputImage is { Length: > 0 };
        var path = editing && ImagesDialectFor(options) == ImagesDialect.Xai
            ? DefaultPath(options.EditPath, "v1/images/edits")
            : DefaultPath(options.GenerationPath, ImageApiFormat.DefaultImagesPath(options.ProviderType, options.BaseUrl));
        var endpoint = NetworkSecurity.CombineEndpoint(options.BaseUrl!, path, "图像服务接入地址");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(BuildRequestBody(options, prompt, inputImage, inputMime))
        };
        ApplyHeaders(request, options);
        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, editing ? "图像编辑" : "图像生成", ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return await ParseImagesAsync(http, doc.RootElement, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object> BuildRequestBody(
        ImageGenerationOptions options, string prompt, byte[]? inputImage, string? inputMime)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model!.Trim(),
            ["prompt"] = prompt.Trim()
        };
        var size = options.Size.Trim();
        var reference = inputImage is { Length: > 0 }
            ? $"data:{inputMime ?? "image/png"};base64,{Convert.ToBase64String(inputImage)}" : null;
        switch (ImagesDialectFor(options))
        {
            case ImagesDialect.OpenRouter:
                body["n"] = 1;
                if (ImagePromptComposer.AspectRatio(size) is { } ratio) body["aspect_ratio"] = ratio;
                body["resolution"] = Resolution(size);
                if (reference is not null) body["input_references"] = new[] { new { type = "image_url", image_url = new { url = reference } } };
                break;
            case ImagesDialect.Xai:
                body["n"] = 1;
                if (ImagePromptComposer.AspectRatio(size) is { } xaiRatio) body["aspect_ratio"] = xaiRatio;
                body["resolution"] = MaxEdge(size) >= 1600 ? "2k" : "1k";
                body["response_format"] = "b64_json";
                if (reference is not null) body["image"] = new { type = "image_url", url = reference };
                break;
            case ImagesDialect.Seedream:
                body["size"] = SeedreamSize(size);
                body["response_format"] = "b64_json";
                body["watermark"] = false;
                if (reference is not null) body["image"] = reference;
                break;
            case ImagesDialect.SiliconFlow:
                if (options.Model?.Contains("qwen-image-edit", StringComparison.OrdinalIgnoreCase) != true)
                    body["image_size"] = size;
                if (reference is not null) body["image"] = reference;
                break;
            case ImagesDialect.RefsInBody:
                body["n"] = 1;
                body["size"] = size;
                if (reference is not null) body["image"] = reference;
                break;
            case ImagesDialect.Zhipu:
                body["size"] = size;
                break;
            default:
                body["n"] = 1;
                body["size"] = size;
                if (options.Model?.Contains("gpt-image", StringComparison.OrdinalIgnoreCase) != true)
                    body["response_format"] = "b64_json";
                break;
        }
        if (ImagePromptComposer.Describe(options, isEdit: false).Style == ImageParameterChannel.Parameter
            && !string.IsNullOrWhiteSpace(options.Style))
        {
            body["style"] = options.Style.Trim();
        }

        return body;
    }

    private enum ImagesDialect { OpenAi, OpenRouter, Xai, Seedream, SiliconFlow, RefsInBody, Zhipu }

    private static ImagesDialect ImagesDialectFor(ImageGenerationOptions options)
    {
        var host = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        var model = options.Model ?? string.Empty;
        var id = model[(model.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (ImageApiFormat.IsOpenRouter(options.BaseUrl)) return ImagesDialect.OpenRouter;
        if (host.EndsWith("siliconflow.cn") || host.EndsWith("siliconflow.com")) return ImagesDialect.SiliconFlow;
        if (host == "api.x.ai" || id.StartsWith("grok-imagine") || id.StartsWith("grok-2-image")) return ImagesDialect.Xai;
        if (host.EndsWith("bigmodel.cn") || id.StartsWith("cogview") || id.StartsWith("glm-image")) return ImagesDialect.Zhipu;
        if (host.EndsWith("volces.com") || id.Contains("seedream")) return ImagesDialect.Seedream;
        return id.StartsWith("qwen-image") || id.StartsWith("wan2") || id.StartsWith("wanx")
            ? ImagesDialect.RefsInBody : ImagesDialect.OpenAi;
    }

    private static (int Width, int Height)? Dimensions(string size)
    {
        var parts = size.Split(['x', 'X', '×']);
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0
            ? (w, h) : null;
    }

    private static int MaxEdge(string size) => Dimensions(size) is { } d ? Math.Max(d.Width, d.Height) : 1024;

    private static string Resolution(string size) => MaxEdge(size) switch { >= 3000 => "4K", >= 1600 => "2K", _ => "1K" };

    private static string SeedreamSize(string size)
    {
        if (Dimensions(size) is not { } d || (long)d.Width * d.Height >= 2560 * 1440) return size;
        var scale = Math.Sqrt(2560d * 1440 / ((long)d.Width * d.Height));
        return $"{(int)Math.Ceiling(d.Width * scale / 16) * 16}x{(int)Math.Ceiling(d.Height * scale / 16) * 16}";
    }

    private static void ApplyHeaders(HttpRequestMessage request, ImageGenerationOptions options)
    {
        if (options.ProviderType == "gemini") request.Headers.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        OpenRouterAttribution.Apply(request, options.BaseUrl, options.CustomHeaders?.ToArray());
        if (options.CustomHeaders is not null)
            foreach (var (name, value) in options.CustomHeaders) request.Headers.TryAddWithoutValidation(name, value);
    }

    private static async Task<IReadOnlyList<GeneratedImage>> ParseImagesAsync(
        HttpClient http,
        JsonElement root,
        CancellationToken ct)
    {
        if ((!root.TryGetProperty("data", out var data) && !root.TryGetProperty("images", out data))
            || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<GeneratedImage>();

        var rootMime = MimeFromOutputFormat(ReadString(root, "output_format"));
        var images = new List<GeneratedImage>();
        foreach (var item in data.EnumerateArray())
        {
            var revisedPrompt = ReadString(item, "revised_prompt");
            if (ReadString(item, "b64_json") is { Length: > 0 } b64)
            {
                images.Add(new GeneratedImage(Convert.FromBase64String(b64), ReadString(item, "media_type") ?? rootMime, revisedPrompt));
                continue;
            }

            if (ReadString(item, "url") is not { Length: > 0 } url)
                continue;

            // Some relays put a data URL where a link is expected.
            if (TryDecodeDataUrl(url, out var inline, out var inlineMime))
            {
                images.Add(new GeneratedImage(inline, inlineMime, revisedPrompt));
                continue;
            }

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
            options.BaseUrl!, DefaultPath(options.ChatPath ?? options.GenerationPath, "v1/chat/completions"), "图像服务接入地址");

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
        var imageConfig = new Dictionary<string, object>();
        if (ImagePromptComposer.AspectRatio(options.Size) is { } ratio) imageConfig["aspect_ratio"] = ratio;
        if (ImageApiFormat.IsOpenRouter(options.BaseUrl) || options.Model?.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase) != true)
            imageConfig["image_size"] = Resolution(options.Size);
        body["image_config"] = imageConfig;
        if (!ImageApiFormat.IsOpenRouter(options.BaseUrl))
            body["extra_body"] = new { google = new { image_config = imageConfig } };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };
        ApplyHeaders(request, options);

        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像生成", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return await ParseChatImagesAsync(http, doc.RootElement, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GeneratedImage>> GenerateViaGeminiAsync(
        ImageGenerationOptions options, string prompt, byte[]? inputImage, string? inputMime, CancellationToken ct)
    {
        var model = options.Model!.Trim();
        if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) model = model[7..];
        var path = DefaultPath(options.ChatPath ?? options.GenerationPath, ImageApiFormat.DefaultChatPath("gemini"))
            .Replace("{model}", Uri.EscapeDataString(model), StringComparison.Ordinal)
            .Replace(":streamGenerateContent", ":generateContent", StringComparison.Ordinal);
        var endpoint = NetworkSecurity.CombineEndpoint(options.BaseUrl!, path, "图像服务接入地址");
        var parts = new List<object>();
        if (inputImage is { Length: > 0 })
            parts.Add(new { inlineData = new { mimeType = inputMime ?? "image/png", data = Convert.ToBase64String(inputImage) } });
        parts.Add(new { text = prompt.Trim() });
        var imageConfig = new Dictionary<string, object>();
        if (ImagePromptComposer.AspectRatio(options.Size) is { } ratio) imageConfig["aspectRatio"] = ratio;
        if (!model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) imageConfig["imageSize"] = Resolution(options.Size);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts } },
                generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" }, imageConfig }
            })
        };
        ApplyHeaders(request, options);
        using var http = _httpFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像生成", ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var images = new List<GeneratedImage>();
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return images;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var outputParts) || outputParts.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in outputParts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inline) && !part.TryGetProperty("inline_data", out inline)) continue;
                if (ReadString(inline, "data") is not { Length: > 0 } b64) continue;
                images.Add(new GeneratedImage(Convert.FromBase64String(b64),
                    ReadString(inline, "mimeType") ?? ReadString(inline, "mime_type") ?? "image/png", null));
            }
        }
        return images;
    }

    private static async Task<IReadOnlyList<GeneratedImage>> ParseChatImagesAsync(HttpClient http, JsonElement root, CancellationToken ct)
    {
        var images = new List<GeneratedImage>();
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;
            var urls = new List<string>();
            if (message.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                    if (ImageUrl(img) is { Length: > 0 } url) urls.Add(url);
            }
            if (urls.Count == 0 && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (ImageUrl(part) is { Length: > 0 } url) urls.Add(url);
                        if (ReadString(part, "text") is { } text) AddTextImages(text, urls);
                    }
                }
                else if (content.ValueKind == JsonValueKind.String) AddTextImages(content.GetString()!, urls);
            }
            foreach (var url in urls.Distinct(StringComparer.Ordinal))
            {
                if (TryDecodeDataUrl(url, out var bytes, out var mime))
                    images.Add(new GeneratedImage(bytes, mime, RevisedPrompt: null));
                else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
                    await ChatApiErrorHelper.EnsureSuccessAsync(response, "图像下载", ct).ConfigureAwait(false);
                    images.Add(new GeneratedImage(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                        response.Content.Headers.ContentType?.MediaType ?? "image/png", null));
                }
            }
        }

        return images;
    }

    private static string? ImageUrl(JsonElement image)
    {
        if (image.ValueKind == JsonValueKind.String) return image.GetString();
        if (image.ValueKind != JsonValueKind.Object) return null;
        if (image.TryGetProperty("image_url", out var url))
            return url.ValueKind == JsonValueKind.String ? url.GetString() : ReadString(url, "url");
        return ReadString(image, "url");
    }

    private static void AddTextImages(string text, List<string> urls)
    {
        foreach (Match match in Regex.Matches(text, @"!\[[^\]]*\]\(([^\s]+?)\)")) urls.Add(match.Groups[1].Value);
        if (urls.Count == 0 && text.TrimStart().StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) urls.Add(text.Trim());
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
        if (options.ProviderType == "gemini" && options.Model.Trim().StartsWith("imagen", StringComparison.OrdinalIgnoreCase)
            || options.ProviderType == "gemini" && options.Model.Trim().StartsWith("models/imagen", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gemini API 已不再支持 Imagen，请选择 Gemini 图像模型。");
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
