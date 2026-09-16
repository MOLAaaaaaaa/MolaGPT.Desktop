using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;

namespace MolaGPT.Core.Chat.Tools;

public sealed class ChatToolHost : IChatToolHost
{
    private readonly McpClientManager _mcp;
    private readonly VisionProxyTool _vision;
    private readonly ImageAnalysisTool _imageAnalysis;
    private readonly ImageGenerationTool _imageGeneration;
    private readonly PythonExecutionTool _python;
    private readonly BrowserControlTool _browser;
    private readonly IToolApprovalService? _approval;
    private readonly BrowserActivityLog? _browserActivity;

    public ChatToolHost(
        McpClientManager mcp,
        VisionProxyTool vision,
        ImageAnalysisTool imageAnalysis,
        ImageGenerationTool imageGeneration,
        PythonExecutionTool python,
        BrowserControlTool browser,
        IToolApprovalService? approval = null,
        BrowserActivityLog? browserActivity = null)
    {
        _mcp = mcp;
        _vision = vision;
        _imageAnalysis = imageAnalysis;
        _imageGeneration = imageGeneration;
        _python = python;
        _browser = browser;
        _approval = approval;
        _browserActivity = browserActivity;
    }

    public async Task<IReadOnlyList<object>> BuildToolDefinitionsAsync(
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        options = WithConversationWorkspace(options, context);
        var tools = new List<object>();

        if (options.Vision?.Enabled == true && !context.ModelSupportsVision)
            tools.Add(VisionProxyTool.BuildOpenAiToolDefinition());

        // Offered regardless of the main model's own vision support: a file in
        // the working directory is not in its context, and a tool result is
        // text, so even a multimodal model has no other way to see it.
        if (ImageAnalysisTool.IsAvailable(options))
            tools.Add(ImageAnalysisTool.BuildOpenAiToolDefinition());

        if (options.ImageGeneration?.Enabled == true && options.ImageGeneration.AsTool)
            tools.Add(ImageGenerationTool.BuildOpenAiToolDefinition());

        if (options.Python?.Enabled == true)
            tools.Add(PythonExecutionTool.BuildOpenAiToolDefinition(options.Python));

        if (options.Browser?.Enabled == true)
            tools.Add(BrowserControlTool.BuildOpenAiToolDefinition(options.Browser));

        foreach (var server in options.McpServers?.Where(s => s.Enabled) ?? Enumerable.Empty<McpServerOptions>())
        {
            try
            {
                tools.AddRange(await _mcp.BuildOpenAiToolDefinitionsAsync(server, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"无法加载 MCP 服务 {server.Name} 的工具：{ex.Message}", ex);
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
        options = WithConversationWorkspace(options, context);

        if (toolName is "search_web" or "web_fetch" or "read_file" or "glob_files" or "grep_files")
        {
            var request = WithWorkspaceScope(
                ToolCapabilityCatalog.ForBuiltIn(toolName, argumentsJson), toolName, argumentsJson, options);
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

        if (string.Equals(toolName, ImageAnalysisTool.ToolName, StringComparison.Ordinal))
        {
            // Same capability set and the same permission mode as the proxy —
            // both amount to "send a picture to the configured vision model", so
            // approving one kind of vision call and being asked again for the
            // other would be noise.
            var request = new ToolApprovalRequest(
                toolName,
                "图片分析",
                ToolCapability.Read | ToolCapability.External,
                argumentsJson,
                "把图片发送给已配置的视觉模型分析");

            // A picture outside the working directory is the user's own file, and
            // this tool does not merely read it — it uploads it. Same prompt as
            // the file tools, for the same reason, on the same resolved path.
            var target = ImageAnalysisTool.ResolveApprovalTarget(argumentsJson, options);
            if (target is not null && !NeedsNoPrompt(options, target))
            {
                request = request with
                {
                    Capabilities = request.Capabilities | ToolCapability.OutsideWorkspace,
                    ResolvedPath = target
                };
            }

            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.VisionPermissionMode), ct).ConfigureAwait(false))
                return PermissionDenied(toolName);
            return await _imageAnalysis.ExecuteAsync(argumentsJson, options, ct).ConfigureAwait(false);
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
            return await _imageGeneration.ExecuteToolAsync(
                argumentsJson, options.ImageGeneration, options.WorkspaceRoot, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, PythonExecutionTool.ToolName, StringComparison.Ordinal))
            return await _python.ExecuteAsync(argumentsJson, options.Python, context.Request.ConversationId, ct).ConfigureAwait(false);

        if (string.Equals(toolName, BrowserControlTool.ToolName, StringComparison.Ordinal))
        {
            // Read actions auto-approve under Approval mode; only navigate/click/
            // fill/close_session carry Write and raise the dialog. The plan also
            // resolves which site the call lands on, so the prompt can name it
            // and a "始终允许" is recorded against that site alone.
            var plan = await _browser
                .PlanApprovalAsync(argumentsJson, options.Browser, context.Request.ConversationId, ct)
                .ConfigureAwait(false);

            if (plan.Refusal is { } refusal)
            {
                RecordBrowserActivity(plan, success: false, note: refusal);
                return BrowserControlTool.Error(refusal);
            }

            if (!plan.PreApproved)
            {
                var mode = EffectiveMode(options.PermissionMode, options.BrowserPermissionMode);
                var request = new ToolApprovalRequest(
                    plan.GrantKey,
                    plan.DisplayName,
                    plan.Capabilities,
                    argumentsJson,
                    "通过本机 Kimi 浏览器扩展操作你的 Chrome/Edge（登录态保留在本机）",
                    // Two different reasons to force the dialog, and they are not
                    // the same rule:
                    //
                    // AlwaysAsk-because-no-host stops an unscoped "始终允许" from
                    // being recorded. It defers to the user's mode — under
                    // FullAccess there is no dialog and so no grant to widen, and
                    // forcing one would just override their choice.
                    //
                    // AlwaysAsk-because-protected ignores the mode on purpose.
                    // Downloads, consent pages and checkout pages are the class of
                    // thing a standing authorization cannot cover: turning on
                    // FullAccess means "stop asking me about clicks", not "put the
                    // payment page through too".
                    AlwaysAsk: plan.IsProtected || (plan.AlwaysAsk && mode == ToolPermissionMode.Approval));
                if (!await IsApprovedAsync(request, mode, ct).ConfigureAwait(false))
                {
                    RecordBrowserActivity(plan, success: false, note: "用户拒绝");
                    return PermissionDenied(toolName);
                }
            }

            var result = await _browser.ExecuteAsync(
                argumentsJson,
                options.Browser,
                context.Request.ConversationId,
                options.WorkspaceRoot,
                ct).ConfigureAwait(false);

            RecordBrowserResult(plan, result);
            return result;
        }

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

    /// <summary>
    /// Flags a read-only file call that resolves outside the conversation's working
    /// directory, and records where it actually lands.
    ///
    /// Inside the workspace these tools stay silent — that directory exists for the
    /// model to work in, and prompting for every file in it would train people to
    /// click through. Leaving it is the part the user has to see, and it is not
    /// something they can judge from the raw arguments: a bare file name, a "..",
    /// and a drive letter all look alike in a JSON blob.
    /// </summary>
    private static ToolApprovalRequest WithWorkspaceScope(
        ToolApprovalRequest request,
        string toolName,
        string argumentsJson,
        LocalToolOptions options)
    {
        if (toolName is not ("read_file" or "glob_files" or "grep_files"))
            return request;

        var target = LocalToolRegistry.ResolveApprovalTarget(toolName, argumentsJson, options);
        if (target is null || NeedsNoPrompt(options, target))
            return request;

        return request with
        {
            Capabilities = request.Capabilities | ToolCapability.OutsideWorkspace,
            ResolvedPath = target
        };
    }

    /// <summary>
    /// Whether a read of <paramref name="target"/> is inside the ground this turn
    /// already covers: the conversation's working directory, or a root the app
    /// itself opened up — the folders of the skills switched on for this turn.
    ///
    /// The skill case is not a concession. Those files are the app's own content,
    /// and the model was handed a catalogue of them in its system prompt; asking
    /// the user to approve reading back something we just advertised is a prompt
    /// with no decision in it, and prompts with no decision in them are how people
    /// learn to click through the ones that matter.
    /// </summary>
    private static bool NeedsNoPrompt(LocalToolOptions options, string target) =>
        WorkspaceScope.IsInside(options.WorkspaceRoot, target)
        || options.ReadableRootList.Any(root => WorkspaceScope.Covers(root, target));

    private static LocalToolOptions WithConversationWorkspace(LocalToolOptions options, ChatToolContext context)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot)
            || string.IsNullOrWhiteSpace(context.Request.ConversationId))
            return options;

        var workspace = PythonExecutionTool.GetSessionDirectory(context.Request.ConversationId);
        Directory.CreateDirectory(workspace);
        return options with { WorkspaceRoot = workspace };
    }

    /// <summary>
    /// 一行流水账。只写元数据——站点、动作、成败、原因；页面内容和填入的值不记，
    /// 那正是这套架构承诺不外流的东西。
    /// </summary>
    private void RecordBrowserActivity(BrowserApprovalPlan plan, bool success, string? note)
    {
        if (_browserActivity is null) return;
        _browserActivity.Record(new BrowserActivityEntry(
            DateTimeOffset.Now,
            plan.Action ?? BrowserControlTool.ToolName,
            plan.Host,
            success,
            note));
    }

    private void RecordBrowserResult(BrowserApprovalPlan plan, string resultJson)
    {
        if (_browserActivity is null) return;

        // 只读动作太频繁，逐条记会把真正值得看的写操作淹掉——除非它们失败了，
        // 那时候用户想知道的恰恰是「什么时候开始连不上的」。
        var isRead = BrowserControlTool.ClassifyAction(plan.Action) == BrowserActionKind.Read;
        var (success, error) = ReadToolOutcome(resultJson);
        if (isRead && success) return;

        RecordBrowserActivity(plan, success, error);
    }

    private static (bool Success, string? Error) ReadToolOutcome(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            var failed = root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            return (!failed, failed ? error : null);
        }
        catch (JsonException)
        {
            return (true, null);
        }
    }

    private async Task<bool> IsApprovedAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        CancellationToken ct)
    {
        if (_approval is null)
        {
            // Nobody to ask. Anything that would have prompted is refused rather
            // than waved through — including a read that leaves the workspace,
            // which is exactly the case that only a human can answer.
            return !request.AlwaysAsk
                   && !request.Capabilities.HasFlag(ToolCapability.Write)
                   && !request.Capabilities.HasFlag(ToolCapability.Destructive)
                   && !request.Capabilities.HasFlag(ToolCapability.OutsideWorkspace);
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
