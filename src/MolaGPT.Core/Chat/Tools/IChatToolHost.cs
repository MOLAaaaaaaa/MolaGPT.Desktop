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

    /// <summary>Presentation hints for the local agent, keyed by tool name. Tools
    /// without an entry are declared to the model directly.</summary>
    Task<IReadOnlyDictionary<string, AgentToolHints>> DescribeAgentToolsAsync(
        LocalToolOptions options,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, AgentToolHints>>(
            new Dictionary<string, AgentToolHints>(StringComparer.Ordinal));
}

public sealed record ChatToolContext(
    ChatRequest Request,
    string ProviderId,
    string ModelId,
    bool ModelSupportsVision,
    IReadOnlyList<ProviderModel> ProviderModels,
    HttpClient? LocalHttpClient = null);
