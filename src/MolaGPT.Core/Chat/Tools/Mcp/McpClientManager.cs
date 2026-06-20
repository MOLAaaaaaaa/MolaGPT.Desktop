using System.Collections.Concurrent;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.Core.Chat.Tools.Mcp;

public sealed class McpClientManager
{
    private readonly McpHttpClient _client;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpServerState>>> _states = new();

    public McpClientManager(McpHttpClient client) => _client = client;

    public async Task<IReadOnlyList<object>> BuildOpenAiToolDefinitionsAsync(
        McpServerOptions server,
        CancellationToken ct)
    {
        var state = await GetStateAsync(server, ct).ConfigureAwait(false);
        return state.Tools.Select(tool => new
        {
            type = "function",
            function = new
            {
                name = McpToolName.Build(server.Id, tool.Name),
                description = string.IsNullOrWhiteSpace(tool.Description)
                    ? $"MCP tool from {server.Name}: {tool.Name}"
                    : tool.Description,
                parameters = tool.InputSchema.ValueKind == JsonValueKind.Object
                    ? tool.InputSchema
                    : JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
            }
        }).Cast<object>().ToArray();
    }

    public async Task<string> CallToolAsync(
        McpServerOptions server,
        string toolSlug,
        string argumentsJson,
        CancellationToken ct)
    {
        var state = await GetStateAsync(server, ct).ConfigureAwait(false);
        var tool = state.Tools.FirstOrDefault(t =>
            string.Equals(McpToolName.Slugify(t.Name), toolSlug, StringComparison.Ordinal));
        if (tool is null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"MCP tool not found: {toolSlug}"
            });
        }

        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var response = await _client.CallToolAsync(state.Session, tool.Name, argsDoc.RootElement, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            success = true,
            source = "mcp",
            server = server.Name,
            tool = tool.Name,
            response
        });
    }

    public async Task<McpToolDescriptor?> GetToolDescriptorAsync(
        McpServerOptions server,
        string toolSlug,
        CancellationToken ct)
    {
        var state = await GetStateAsync(server, ct).ConfigureAwait(false);
        return state.Tools.FirstOrDefault(t =>
            string.Equals(McpToolName.Slugify(t.Name), toolSlug, StringComparison.Ordinal));
    }

    private Task<McpServerState> GetStateAsync(McpServerOptions server, CancellationToken ct)
    {
        var key = $"{server.Id}|{server.Url}|{server.HeaderName}|{server.Token}";
        var lazy = _states.GetOrAdd(key, _ => new Lazy<Task<McpServerState>>(() => CreateStateAsync(server, ct)));
        return lazy.Value;
    }

    private async Task<McpServerState> CreateStateAsync(McpServerOptions server, CancellationToken ct)
    {
        var session = await _client.InitializeAsync(server, ct).ConfigureAwait(false);
        var tools = await _client.ListToolsAsync(session, ct).ConfigureAwait(false);
        return new McpServerState(session, tools);
    }

    private sealed record McpServerState(McpSession Session, IReadOnlyList<McpToolDescriptor> Tools);
}
