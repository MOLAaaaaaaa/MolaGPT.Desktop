namespace MolaGPT.Core.Models;

public static class ProviderPurpose
{
    public const string Chat = "chat";
    public const string Image = "image";

    public static bool IsImage(string? purpose) =>
        string.Equals(purpose, Image, StringComparison.OrdinalIgnoreCase);
}

public sealed record ProviderEntry(
    string Id,
    string Type,                                           // 协议族: openai-compat|anthropic|gemini
    string Name,
    string? BaseUrl,
    string? ApiKey,
    List<ProviderModelEntry> Models,
    bool Enabled,
    int SortOrder,
    string Purpose = ProviderPurpose.Chat,                               // 用途: chat|image
    string? ApiPath = null,                                // chat: 对话路径; image: 生成路径 (含版本段)
    string? ImageEditPath = null,                          // image(openai-images): 编辑路径
    string? ImageFormat = null,                            // image: openai-images|openai-chat-image
    List<CustomHeaderEntry>? CustomHeaders = null,
    string? ImageChatPath = null,
    bool BuiltinSearch = false);

public sealed record ProviderModelEntry(
    string Id,
    string DisplayName,
    bool Vision = false,
    int? ContextWindow = null,
    bool Thinking = false,
    bool ReasoningEffort = false,
    bool Tools = false,
    string? ThinkingParamKind = null,
    int? ThinkingBudgetMin = null,
    int? ThinkingBudgetMax = null,
    int? ThinkingBudgetDefault = null,
    string? DefaultEffort = null,
    bool ImageEdit = false,
    List<CustomBodyEntry>? CustomBody = null,
    List<string>? EffortLevels = null,
    /// <summary>The provider declares this model always reasons (OpenRouter's
    /// <c>reasoning.mandatory</c>). No disable parameter may be sent.</summary>
    bool ReasoningMandatory = false,
    bool SupportsTemperature = true,
    bool SupportsTopP = true,
    ModelPricing? Pricing = null,
    string? ImageFormat = null,
    bool? BuiltinSearch = null);

/// <summary>A user-defined HTTP header appended to a BYOK provider's requests.</summary>
public sealed record CustomHeaderEntry(string Name = "", string Value = "");

/// <summary>A user-defined request-body override for a BYOK model. <see cref="Type"/>
/// is one of string|number|boolean|json and controls how <see cref="Value"/> is parsed.</summary>
public sealed record CustomBodyEntry(string Key = "", string Type = "string", string Value = "");
