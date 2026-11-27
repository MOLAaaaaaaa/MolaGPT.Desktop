using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;

namespace MolaGPT.Desktop.Services;

public sealed class ProviderConnection(IHttpClientFactory httpClients)
{
    private const string AnthropicVersion = "2023-06-01";

    public async Task TestChatAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        using var http = httpClients.CreateClient(HttpClientNames.Byok);
        var baseUrl = NetworkSecurity.RequireHttpOrHttpsBaseUrl(entry.BaseUrl ?? string.Empty, $"{entry.Name} 接入地址");
        var path = string.IsNullOrWhiteSpace(entry.ApiPath)
            ? DefaultApiPath(entry.Purpose, entry.Type)
            : entry.ApiPath;
        var model = entry.Models.FirstOrDefault()?.Id ?? DefaultTestModel(entry.Type);
        var nativeGemini = string.Equals(entry.Type, "gemini", StringComparison.OrdinalIgnoreCase);
        var googleCompat = GeminiModelPolicy.IsOpenAiCompatRoute(entry.Type, baseUrl, path);
        var requestPath = nativeGemini ? path.Replace("{model}", Uri.EscapeDataString(model), StringComparison.Ordinal) : path;
        var url = NetworkSecurity.CombineEndpoint(baseUrl, requestPath, $"{entry.Name} 接入地址");
        if (nativeGemini) url = new Uri(AppendAltSse(url.ToString()));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var messages = new[] { new { role = "user", content = "ping" } };
        object body = nativeGemini
            ? new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } }
            : googleCompat ? new { model, messages, max_tokens = 4, stream = true }
            : entry.Type switch
            {
                "anthropic" => new { model, max_tokens = 8, messages },
                "openai-response" => new { model, input = "ping", max_output_tokens = 16 },
                _ => new { model, messages, max_tokens = 4 }
            };
        request.Content = JsonContent.Create(body);
        if (nativeGemini || googleCompat)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyProviderAuthentication(request, entry);
        ApplyProviderCustomHeaders(request, entry);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{error}", null, response.StatusCode);
        }
    }

    public static string DefaultApiPath(string? purpose, string? type, string? baseUrl = null)
    {
        if (ProviderPurpose.IsImage(purpose))
            return ImageApiFormat.DefaultImagesPath(baseUrl);
        return type?.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "v1/messages",
            "gemini" => "models/{model}:streamGenerateContent",
            "openai-response" => "v1/responses",
            _ => "v1/chat/completions"
        };
    }

    public async Task<List<ProviderModelEntry>> FetchModelsAsync(ProviderEntry entry, string? modelsPath, CancellationToken ct = default)
    {
        var baseUrl = NetworkSecurity.RequireHttpOrHttpsBaseUrl(entry.BaseUrl ?? string.Empty, $"{entry.Name} 接入地址");
        var image = ProviderPurpose.IsImage(entry.Purpose);
        var openRouter = OpenRouterAttribution.IsOpenRouterHost(baseUrl);
        if (image && openRouter) return await FetchOpenRouterImageModelsAsync(entry, baseUrl, ct).ConfigureAwait(false);
        modelsPath ??= GeminiModelPolicy.IsRoute(entry.Type, baseUrl) ? "models" : "v1/models";

        using var document = await FetchModelsDocumentAsync(entry, baseUrl, modelsPath, ct).ConfigureAwait(false);
        if (image) return ParseImageModels(document.RootElement, entry);
        if (openRouter) return ParseOpenRouterModels(document.RootElement);
        return entry.Type == "gemini"
            ? ParseGeminiModels(document.RootElement)
            : ParseCompatibleModels(document.RootElement, entry);
    }

    private async Task<JsonDocument> FetchModelsDocumentAsync(ProviderEntry entry, string baseUrl, string path, CancellationToken ct)
    {
        var url = NetworkSecurity.CombineEndpoint(baseUrl, path, $"{entry.Name} 接入地址");
        using var http = httpClients.CreateClient(HttpClientNames.Byok);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyProviderAuthentication(request, entry);
        ApplyProviderCustomHeaders(request, entry);
        // Anthropic relays may use Bearer authentication for their model catalog.
        if (entry.Type == "anthropic" && !string.IsNullOrWhiteSpace(entry.ApiKey)
            && !request.Headers.Contains("Authorization"))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{body}", null, response.StatusCode);
        return JsonDocument.Parse(body);
    }

    /// <summary>OpenRouter lists its image API models and its image-output chat models in
    /// two catalogs. A model in both can use either API, so it follows the provider default.</summary>
    private async Task<List<ProviderModelEntry>> FetchOpenRouterImageModelsAsync(ProviderEntry entry, string baseUrl, CancellationToken ct)
    {
        using var imagesDoc = await FetchModelsDocumentAsync(entry, baseUrl, "v1/images/models", ct).ConfigureAwait(false);
        using var chatDoc = await FetchModelsDocumentAsync(entry, baseUrl, "v1/models?output_modalities=image", ct).ConfigureAwait(false);
        var images = ParseImageModels(imagesDoc.RootElement, entry, ImageApiFormat.OpenAiImages)
            .ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        var chat = ParseImageModels(chatDoc.RootElement, entry, ImageApiFormat.OpenAiChatImage)
            .Where(model => !model.Id.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        // The general catalog also lists image-only models; only those that also output
        // text can really use both APIs.
        var multimodal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chatDoc.RootElement.TryGetProperty("data", out var chatData) && chatData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in chatData.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                    && ReadStringArray(item, "architecture", "output_modalities").Contains("text"))
                    multimodal.Add(idNode.GetString()!);
            }
        }
        var models = new List<ProviderModelEntry>();
        foreach (var id in images.Keys.Union(chat.Keys, StringComparer.OrdinalIgnoreCase))
        {
            images.TryGetValue(id, out var viaImages);
            chat.TryGetValue(id, out var viaChat);
            models.Add(viaImages is not null && viaChat is not null && multimodal.Contains(id)
                ? viaImages with { ImageFormat = null, ImageEdit = viaImages.ImageEdit || viaChat.ImageEdit }
                : viaImages is not null
                    ? viaImages with { ImageFormat = string.Equals(entry.ImageFormat, ImageApiFormat.OpenAiImages, StringComparison.OrdinalIgnoreCase) ? null : ImageApiFormat.OpenAiImages }
                    : viaChat!);
        }
        return models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ApplyProviderAuthentication(HttpRequestMessage request, ProviderEntry entry)
    {
        if (entry.Type == "anthropic")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", entry.ApiKey ?? string.Empty);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else if (entry.Type == "gemini")
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", entry.ApiKey ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey);
        }
    }

    private static void ApplyProviderCustomHeaders(HttpRequestMessage request, ProviderEntry entry)
    {
        var headers = CustomParamConverter.ToHeaderList(entry.CustomHeaders);
        OpenRouterAttribution.Apply(request, entry.BaseUrl, headers);
        if (headers is null) return;
        foreach (var (name, value) in headers)
            if (!string.IsNullOrWhiteSpace(name)) request.Headers.TryAddWithoutValidation(name, value);
    }

    public static string AppendAltSse(string endpoint) =>
        endpoint.Contains("alt=sse", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : endpoint + (endpoint.Contains('?') ? "&" : "?") + "alt=sse";

    private static string DefaultTestModel(string type) => type switch
    {
        "anthropic" => "claude-3-5-haiku-20241022",
        "gemini" => "gemini-2.5-flash",
        _ => "gpt-4o-mini"
    };

    private static List<ProviderModelEntry> ParseCompatibleModels(JsonElement root, ProviderEntry entry)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var input = ReadStringArray(item, "architecture", "input_modalities");
            var output = ReadStringArray(item, "architecture", "output_modalities");
            var chat = input.Count > 0 && output.Count > 0
                ? input.Any(value => IsAny(value, "text")) && output.Any(value => IsAny(value, "text"))
                : LooksLikeChatModel(id);
            if (!chat) continue;
            var display = item.TryGetProperty("display_name", out var displayNode) && displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : null;
            var preset = ThinkingParamKindInference.DefaultConfigFor(id, entry.BaseUrl);
            var kind = preset?.Kind ?? ThinkingParamKind.None;
            var parameters = ReadStringArray(item, "supported_parameters");
            var hasParameters = item.TryGetProperty("supported_parameters", out var declaredParameters)
                && declaredParameters.ValueKind == JsonValueKind.Array;
            var modalities = ReadStringArray(item, "architecture", "input_modalities");
            var capability = ReadReasoningCapability(item);
            if (hasParameters)
            {
                var declared = capability.Declared || capability.SupportsEffort ||
                    parameters.Any(value => IsAny(value, "reasoning", "reasoning_effort", "include_reasoning"));
                if (!declared) { kind = ThinkingParamKind.None; preset = null; }
                else if (kind == ThinkingParamKind.None) kind = ThinkingParamKind.OpenAiReasoningEffort;
            }
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(display) ? ProviderModelResolver.BeautifyModelName(id) : display!,
                Vision: modalities.Count > 0 ? modalities.Any(value => IsAny(value, "image", "vision")) : LooksLikeVisionModel(id),
                Thinking: kind != ThinkingParamKind.None,
                ReasoningEffort: kind is not (ThinkingParamKind.None or ThinkingParamKind.KimiToggle),
                Tools: hasParameters ? parameters.Any(value => IsAny(value, "tools", "tool_choice")) : LooksLikeToolModel(id),
                ThinkingParamKind: kind.ToString(),
                EffortLevels: capability.Efforts.Count > 0 ? capability.Efforts.ToList() : preset?.EffortLevels?.ToList(),
                DefaultEffort: capability.DefaultEffort ?? preset?.DefaultEffort,
                ReasoningMandatory: capability.Declared ? capability.Mandatory : preset?.Mandatory == true));
        }
        return models.OrderByDescending(model => model.Tools).ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ProviderModelEntry> ParseOpenRouterModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : ProviderModelResolver.BeautifyModelName(id);
            var parameters = ReadStringArray(item, "supported_parameters");
            var modalities = ReadStringArray(item, "architecture", "input_modalities");
            var context = item.TryGetProperty("context_length", out var contextNode) && contextNode.ValueKind == JsonValueKind.Number
                ? contextNode.GetInt32()
                : (int?)null;
            var capability = ReadReasoningCapability(item);
            var hasParameters = item.TryGetProperty("supported_parameters", out var declaredParameters)
                && declaredParameters.ValueKind == JsonValueKind.Array;
            var effort = capability.SupportsEffort || parameters.Any(value => IsAny(value, "reasoning_effort", "reasoning_effort_max"));
            var reasoning = capability.Declared || effort || parameters.Any(value => IsAny(value, "reasoning", "include_reasoning"))
                || !hasParameters && ThinkingParamKindInference.InferFromModelId(id) != ThinkingParamKind.None;
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name!,
                Vision: modalities.Count > 0 ? modalities.Any(value => IsAny(value, "image", "vision")) : LooksLikeVisionModel(id),
                ContextWindow: context,
                Thinking: reasoning,
                ReasoningEffort: effort || !hasParameters && reasoning,
                Tools: hasParameters ? parameters.Any(value => IsAny(value, "tools", "tool_choice")) : LooksLikeToolModel(id),
                ThinkingParamKind: (reasoning ? ThinkingParamKind.OpenAiReasoningEffort : ThinkingParamKind.None).ToString(),
                EffortLevels: capability.Efforts.Count > 0 ? capability.Efforts.ToList() : null,
                ReasoningMandatory: capability.Mandatory,
                DefaultEffort: capability.DefaultEffort,
                SupportsTemperature: SupportsSampling(parameters, "temperature"),
                SupportsTopP: SupportsSampling(parameters, "top_p", "topP"),
                Pricing: ReadEndpointPricing(item)));
        }
        return models.OrderByDescending(model => model.Tools)
            .ThenByDescending(model => model.Thinking)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ProviderModelEntry> ParseGeminiModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
            var methods = ReadStringArray(item, "supportedGenerationMethods");
            if (methods.Count > 0 && !methods.Any(method =>
                    method.Equals("generateContent", StringComparison.OrdinalIgnoreCase)
                    || method.Equals("streamGenerateContent", StringComparison.OrdinalIgnoreCase)))
                continue;
            var display = item.TryGetProperty("displayName", out var displayNode) && displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : ProviderModelResolver.BeautifyModelName(id);
            var declared = item.TryGetProperty("thinking", out var value) ? value.ValueKind : JsonValueKind.Undefined;
            var kind = declared == JsonValueKind.False ? ThinkingParamKind.None : ThinkingParamKindInference.InferFromModelId(id);
            var reasoning = declared == JsonValueKind.True || kind != ThinkingParamKind.None;
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(display) ? ProviderModelResolver.BeautifyModelName(id) : display!,
                Vision: LooksLikeVisionModel(id),
                Thinking: reasoning,
                ReasoningEffort: reasoning,
                Tools: LooksLikeToolModel(id),
                ThinkingParamKind: declared == JsonValueKind.False || kind != ThinkingParamKind.None ? kind.ToString() : null));
        }
        return models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Image models from a model list. <paramref name="catalogFormat"/> is set when the list
    /// itself says which API its models use; otherwise relays' <c>supported_endpoint_types</c>
    /// and the model name decide. A format equal to the provider default is stored as null
    /// (follow the provider). Gemini always uses generateContent and has no per-model choice.
    /// </summary>
    private static List<ProviderModelEntry> ParseImageModels(JsonElement root, ProviderEntry entry, string? catalogFormat = null)
    {
        if ((!root.TryGetProperty("data", out var data) && !root.TryGetProperty("models", out data))
            || data.ValueKind != JsonValueKind.Array) return [];
        var gemini = entry.Type == "gemini";
        var providerFormat = entry.ImageFormat ?? ImageApiFormat.OpenAiImages;
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var hasId = item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String;
            var raw = hasId
                ? idNode.GetString()
                : item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var id = raw.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? raw[7..] : raw;
            // Imagen is no longer served by the Gemini API.
            if (gemini && id.StartsWith("imagen", StringComparison.OrdinalIgnoreCase)) continue;
            var declaredImagesApi = ReadStringArray(item, "supported_endpoint_types").Contains("image-generation");
            if (catalogFormat is null && !declaredImagesApi && !LooksLikeImageModel(id)) continue;

            string? format = null;
            if (!gemini)
            {
                var inferred = catalogFormat
                    ?? (declaredImagesApi ? ImageApiFormat.OpenAiImages
                        : ImageApiFormat.LooksLikeChatImageModel(id) ? ImageApiFormat.OpenAiChatImage : null);
                format = inferred is not null && !string.Equals(inferred, providerFormat, StringComparison.OrdinalIgnoreCase)
                    ? inferred : null;
            }
            var edit = catalogFormat == ImageApiFormat.OpenAiImages
                ? item.TryGetProperty("supported_parameters", out var parameters)
                  && parameters.ValueKind == JsonValueKind.Object
                  && parameters.TryGetProperty("input_references", out _)
                : LooksLikeImageEditModel(id);
            var display = item.TryGetProperty("displayName", out var displayNode) && displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : hasId && item.TryGetProperty("name", out var labelNode) && labelNode.ValueKind == JsonValueKind.String
                    ? labelNode.GetString()
                    : null;
            models.Add(new ProviderModelEntry(id, string.IsNullOrWhiteSpace(display) ? ProviderModelResolver.BeautifyModelName(id) : display!,
                ImageEdit: edit, ImageFormat: format));
        }
        return models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The per-model <c>reasoning</c> block an OpenRouter-style model list carries:
    /// <c>{ mandatory, default_enabled, supported_efforts, default_effort }</c>.
    ///
    /// Worth reading rather than guessing from the name: it is the only authoritative
    /// answer to "which efforts does this model actually take" and "can reasoning be
    /// switched off at all". Non-reasoning and dynamic-router models omit the block,
    /// which leaves the defaults here and the old name-based inference in charge.
    /// </summary>
    private static (bool Declared, bool Mandatory, bool SupportsEffort, IReadOnlyList<string> Efforts, string? DefaultEffort) ReadReasoningCapability(
        JsonElement item)
    {
        if (!item.TryGetProperty("reasoning", out var node) || node.ValueKind != JsonValueKind.Object)
            return (false, false, false, [], null);

        var mandatory = node.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
        var efforts = ReadStringArray(node, "supported_efforts");
        var defaultEffort = node.TryGetProperty("default_effort", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        var supportsEffort = efforts.Count > 0
            || node.TryGetProperty("supported_efforts", out var supported) && supported.ValueKind == JsonValueKind.Null;
        return (true, mandatory, supportsEffort, efforts, string.IsNullOrWhiteSpace(defaultEffort) ? null : defaultEffort);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
            if (!current.TryGetProperty(segment, out current)) return [];
        if (current.ValueKind != JsonValueKind.Array) return [];
        return current.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool LooksLikeChatModel(string id)
    {
        if (ModelCapabilityCatalog.Find(id) is { } known) return known.Chat;
        var value = id.ToLowerInvariant();
        if (value.Contains("embedding") || value.Contains("moderation") || value.Contains("tts")
            || value.Contains("transcribe") || value.Contains("whisper") || value.Contains("dall-e")
            || value.Contains("image") || value.Contains("audio") || value.Contains("realtime")) return false;
        var leaf = value.Split('/')[^1];
        string[] prefixes = ["gpt-", "o1", "o3", "o4", "deepseek", "moonshot", "kimi", "qwen", "qwq",
            "gemini", "claude", "llama", "mistral", "mixtral", "grok", "glm", "minimax", "mimo", "step-", "yi-", "ernie"];
        return prefixes.Any(prefix => leaf.StartsWith(prefix, StringComparison.Ordinal))
            || value.Contains("chat") || value.Contains("instruct");
    }

    private static bool LooksLikeVisionModel(string id)
    {
        if (ModelCapabilityCatalog.Find(id) is { } known) return known.Vision;
        var value = id.ToLowerInvariant();
        return value.Contains("vision") || value.Contains("gpt-4o") || value.Contains("gpt-4.1")
               || value.Contains("gpt-5") || value.Contains("gemini") || value.Contains("claude-3")
               || value.Contains("claude-sonnet-4") || value.Contains("claude-opus-4")
               || value.Contains("claude-haiku-4") || value.Contains("deepseek-chat") || value.Contains("qwen-vl");
    }

    // Same rules as the mobile client's looksLikeByokImageModel / looksLikeByokImageEditModel.
    private static bool LooksLikeImageModel(string id)
    {
        var value = id.ToLowerInvariant();
        if (value.Contains("video")) return false;
        string[] markers =
        [
            "dall-e", "image", "imagen", "flux", "midjourney", "sdxl", "stable-diffusion", "recraft",
            "seedream", "seededit", "nano-banana", "cogview", "kolors", "grok-imagine", "riverflow",
            "ideogram", "jimeng", "hidream", "z-image", "wanx"
        ];
        return markers.Any(value.Contains)
               || System.Text.RegularExpressions.Regex.IsMatch(value[(value.LastIndexOf('/') + 1)..], @"^wan\d.*(?:t2i|image)");
    }

    private static bool LooksLikeImageEditModel(string id)
    {
        var value = id.ToLowerInvariant();
        return value.Contains("gpt-image") || value.Contains("gpt image") || value.Contains("edit")
               || value.Contains("inpaint") || value.Contains("kontext") || value.Contains("seededit")
               || value.Contains("nano-banana") || value.Contains("grok-imagine")
               || value.Contains("seedream") && !value.Contains("t2i")
               || System.Text.RegularExpressions.Regex.IsMatch(value, @"qwen-image-(?:edit|[23])|sora[-_]image|gemini-.*image");
    }

    private static bool LooksLikeToolModel(string id)
    {
        if (ModelCapabilityCatalog.Find(id) is { } known) return known.Tools;
        var value = id.ToLowerInvariant();
        return LooksLikeChatModel(id) && !value.Contains("instruct") && !value.Contains("base");
    }

    private static bool IsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pricing straight out of the model list an OpenRouter-style endpoint already
    /// returned, so it costs no extra request and the ids line up by construction.
    ///
    /// The wire unit is USD per single token, written as a string
    /// (<c>"prompt": "0.0000025"</c>); <see cref="ModelPricing"/> is per million, so
    /// everything is scaled by 1e6 on the way in. Getting that scale wrong is a
    /// six-orders-of-magnitude error that still renders as a plausible-looking price.
    /// </summary>
    private static ModelPricing? ReadEndpointPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return null;
        var input = ReadPerTokenPrice(pricing, "prompt", "input");
        var output = ReadPerTokenPrice(pricing, "completion", "output");
        // Free models are listed with "0" across the board. That is a real price,
        // but a provider that reports nothing usable should stay unpriced rather
        // than claim everything it serves is free.
        if (input is null || output is null) return null;
        return new ModelPricing(
            input.Value,
            output.Value,
            ReadPerTokenPrice(pricing, "input_cache_read", "cache_read"),
            ReadPerTokenPrice(pricing, "input_cache_write", "cache_write"),
            ModelPricing.SourceEndpoint);
    }

    private static double? ReadPerTokenPrice(JsonElement pricing, params string[] names)
    {
        foreach (var name in names)
        {
            if (!pricing.TryGetProperty(name, out var node)) continue;
            double perToken;
            switch (node.ValueKind)
            {
                case JsonValueKind.Number when node.TryGetDouble(out var number):
                    perToken = number;
                    break;
                case JsonValueKind.String when double.TryParse(
                    node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    perToken = parsed;
                    break;
                default:
                    continue;
            }
            if (perToken < 0) continue;
            return perToken * 1_000_000d;
        }
        return null;
    }

    private static bool SupportsSampling(IReadOnlyList<string> parameters, params string[] names) =>
        parameters.Any(value => IsAny(value, names));
}
