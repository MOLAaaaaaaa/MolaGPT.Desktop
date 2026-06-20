using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Tools;

public interface IChatToolHost
{
    Task<IReadOnlyList<object>> BuildToolDefinitionsAsync(
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct);

    Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct);
}

public sealed record ChatToolContext(
    ChatRequest Request,
    string ProviderId,
    string ModelId,
    bool ModelSupportsVision,
    IReadOnlyList<ProviderModel> ProviderModels,
    HttpClient? LocalHttpClient = null);
