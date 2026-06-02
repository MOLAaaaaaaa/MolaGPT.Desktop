using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.Vision;

namespace MolaGPT.Core.Chat.Tools;

public sealed class ChatToolHost : IChatToolHost
{
    private readonly McpClientManager _mcp;
    private readonly VisionProxyTool _vision;
    private readonly ImageGenerationTool _imageGeneration;

    public ChatToolHost(McpClientManager mcp, VisionProxyTool vision, ImageGenerationTool imageGeneration)
    {
        _mcp = mcp;
        _vision = vision;
        _imageGeneration = imageGeneration;
    }

    public async Task<IReadOnlyList<object>> BuildToolDefinitionsAsync(
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        var tools = new List<object>();

        if (options.Vision?.Enabled == true && !context.ModelSupportsVision)
            tools.Add(VisionProxyTool.BuildOpenAiToolDefinition());

        if (options.ImageGeneration?.Enabled == true && options.ImageGeneration.AsTool)
            tools.Add(ImageGenerationTool.BuildOpenAiToolDefinition());

        foreach (var server in options.McpServers?.Where(s => s.Enabled) ?? Enumerable.Empty<McpServerOptions>())
        {
            try
            {
                tools.AddRange(await _mcp.BuildOpenAiToolDefinitionsAsync(server, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken MCP server should not prevent the chat request from starting.
            }
        }

        return tools;
    }

    public Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        if (string.Equals(toolName, VisionProxyTool.ToolName, StringComparison.Ordinal))
            return _vision.ExecuteAsync(argumentsJson, context, options.Vision, ct);

        if (string.Equals(toolName, ImageGenerationTool.ToolName, StringComparison.Ordinal))
            return _imageGeneration.ExecuteToolAsync(argumentsJson, options.ImageGeneration, ct);

        if (McpToolName.TryDecode(toolName, out var serverSlug, out var toolSlug))
            return ExecuteMcpAsync(serverSlug, toolSlug, argumentsJson, options, ct);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            success = false,
            error = $"Unknown tool: {toolName}"
        }));
    }

    private async Task<string> ExecuteMcpAsync(
        string serverSlug,
        string toolSlug,
        string argumentsJson,
        LocalToolOptions options,
        CancellationToken ct)
    {
        var server = options.McpServers?
            .Where(s => s.Enabled)
            .FirstOrDefault(s => string.Equals(McpToolName.Slugify(s.Id), serverSlug, StringComparison.Ordinal));

        if (server is null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"MCP server not found: {serverSlug}"
            });
        }

        try
        {
            return await _mcp.CallToolAsync(server, toolSlug, argumentsJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
