namespace MolaGPT.Storage;

public sealed record ConversationRow
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ModelId { get; set; }
    public string? ProviderId { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Pinned { get; set; }
    public long? DeletedAt { get; set; }
    public string? SystemPrompt { get; set; }
    public string? PersonaId { get; set; }
    public string? SystemPromptMode { get; set; }

    public ConversationRow() { }

    public ConversationRow(
        string Id,
        string Title,
        string? ModelId,
        string? ProviderId,
        long CreatedAt,
        long UpdatedAt,
        bool Pinned,
        long? DeletedAt,
        string? SystemPrompt = null,
        string? PersonaId = null,
        string? SystemPromptMode = null)
    {
        this.Id = Id;
        this.Title = Title;
        this.ModelId = ModelId;
        this.ProviderId = ProviderId;
        this.CreatedAt = CreatedAt;
        this.UpdatedAt = UpdatedAt;
        this.Pinned = Pinned;
        this.DeletedAt = DeletedAt;
        this.SystemPrompt = SystemPrompt;
        this.PersonaId = PersonaId;
        this.SystemPromptMode = SystemPromptMode;
    }
}

public sealed record PersonaRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public bool? DefaultEnableNetwork { get; set; }
    public bool? DefaultEnableWebFetch { get; set; }
    public bool? DefaultThinking { get; set; }
    public string? DefaultReasoningEffort { get; set; }
    public int SortOrder { get; set; }
    public bool Pinned { get; set; }
    public bool IsBuiltin { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }
}

public sealed record MessageRow(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    string? Meta,
    long CreatedAt);

public sealed record ImageWorkbenchMessageRow(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    string? Meta,
    long CreatedAt,
    string ConversationTitle);

public sealed record ProviderRow
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public byte[]? ApiKeyEnc { get; set; }
    public string Models { get; set; } = "[]";
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }
    public string Purpose { get; set; } = "chat";
    public string? ApiPath { get; set; }
    public string? ImageEditPath { get; set; }
    public string? ImageFormat { get; set; }
    public string? CustomHeaders { get; set; }

    public ProviderRow() { }

    public ProviderRow(
        string Id,
        string Type,
        string Name,
        string? BaseUrl,
        byte[]? ApiKeyEnc,
        string Models,
        bool Enabled,
        int SortOrder,
        string Purpose = "chat",
        string? ApiPath = null,
        string? ImageEditPath = null,
        string? ImageFormat = null,
        string? CustomHeaders = null)
    {
        this.Id = Id;
        this.Type = Type;
        this.Name = Name;
        this.BaseUrl = BaseUrl;
        this.ApiKeyEnc = ApiKeyEnc;
        this.Models = Models;
        this.Enabled = Enabled;
        this.SortOrder = SortOrder;
        this.Purpose = Purpose;
        this.ApiPath = ApiPath;
        this.ImageEditPath = ImageEditPath;
        this.ImageFormat = ImageFormat;
        this.CustomHeaders = CustomHeaders;
    }
}
