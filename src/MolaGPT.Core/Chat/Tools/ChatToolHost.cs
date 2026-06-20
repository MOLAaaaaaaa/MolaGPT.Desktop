using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;

namespace MolaGPT.Core.Chat.Tools;

public sealed class ChatToolHost : IChatToolHost
{
    private readonly McpClientManager _mcp;
    private readonly VisionProxyTool _vision;
    private readonly ImageGenerationTool _imageGeneration;
    private readonly PythonExecutionTool _python;
    private readonly IToolApprovalService? _approval;

    public ChatToolHost(
        McpClientManager mcp,
        VisionProxyTool vision,
        ImageGenerationTool imageGeneration,
        PythonExecutionTool python,
        IToolApprovalService? approval = null)
    {
        _mcp = mcp;
        _vision = vision;
        _imageGeneration = imageGeneration;
        _python = python;
        _approval = approval;
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

        if (options.Python?.Enabled == true)
            tools.Add(PythonExecutionTool.BuildOpenAiToolDefinition(options.Python));

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

    public async Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        if (toolName is "search_web" or "web_fetch" or "read_file" or "glob_files" or "grep_files")
        {
            var request = ToolCapabilityCatalog.ForBuiltIn(toolName, argumentsJson);
            if (!await IsApprovedAsync(request, options.PermissionMode, ct).ConfigureAwait(false))
                return PermissionDenied(toolName);
            if (context.LocalHttpClient is null)
                return ToolError("Local HTTP client is unavailable.");
            return await LocalToolRegistry.ExecuteAsync(
                toolName, argumentsJson, options, context.LocalHttpClient, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, VisionProxyTool.ToolName, StringComparison.Ordinal))
        {
            var request = new ToolApprovalRequest(
                toolName,
                "视觉识别",
                ToolCapability.Read | ToolCapability.External,
                argumentsJson,
                "把当前对话中的图片发送给已配置的视觉模型分析");
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.VisionPermissionMode), ct).ConfigureAwait(false))
                return PermissionDenied(toolName);
            return await _vision.ExecuteAsync(argumentsJson, context, options.Vision, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, ImageGenerationTool.ToolName, StringComparison.Ordinal))
        {
            var request = new ToolApprovalRequest(
                toolName,
                "图像生成",
                ToolCapability.Write | ToolCapability.External,
                argumentsJson,
                "调用外部图像服务并在本地创建图片");
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.ImageGenerationPermissionMode), ct).ConfigureAwait(false))
                return PermissionDenied(toolName);
            return await _imageGeneration.ExecuteToolAsync(argumentsJson, options.ImageGeneration, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, PythonExecutionTool.ToolName, StringComparison.Ordinal))
            return await _python.ExecuteAsync(argumentsJson, options.Python, context.Request.ConversationId, ct).ConfigureAwait(false);

        if (McpToolName.TryDecode(toolName, out var serverSlug, out var toolSlug))
            return await ExecuteMcpAsync(serverSlug, toolSlug, argumentsJson, options, ct).ConfigureAwait(false);

        return ToolError($"Unknown tool: {toolName}");
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
            var descriptor = await _mcp.GetToolDescriptorAsync(server, toolSlug, ct).ConfigureAwait(false);
            if (descriptor is null)
                return ToolError($"MCP tool not found: {toolSlug}");

            var request = new ToolApprovalRequest(
                McpToolName.Build(server.Id, descriptor.Name),
                $"MCP：{server.Name} / {descriptor.Name}",
                descriptor.Capabilities,
                argumentsJson,
                descriptor.Description);
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.McpPermissionMode), ct).ConfigureAwait(false))
                return PermissionDenied(request.ToolName);

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

    private async Task<bool> IsApprovedAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        CancellationToken ct)
    {
        if (_approval is null)
        {
            return !request.AlwaysAsk
                   && !request.Capabilities.HasFlag(ToolCapability.Write)
                   && !request.Capabilities.HasFlag(ToolCapability.Destructive);
        }

        return await _approval.RequestApprovalAsync(request, mode, ct).ConfigureAwait(false)
            == ToolApprovalDecision.Approved;
    }

    private static string PermissionDenied(string toolName) => JsonSerializer.Serialize(new
    {
        success = false,
        error = $"工具调用已被权限策略拒绝：{toolName}",
        permission = "denied"
    });

    private static string ToolError(string message) => JsonSerializer.Serialize(new
    {
        success = false,
        error = message
    });

    /// <summary>Global FullAccess overrides per-tool; per-tool FullAccess overrides global Approval.</summary>
    private static ToolPermissionMode EffectiveMode(ToolPermissionMode global, ToolPermissionMode perTool) =>
        global == ToolPermissionMode.FullAccess || perTool == ToolPermissionMode.FullAccess
            ? ToolPermissionMode.FullAccess
            : ToolPermissionMode.Approval;
}
