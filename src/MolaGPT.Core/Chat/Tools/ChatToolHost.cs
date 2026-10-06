using System.Runtime.CompilerServices;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;
using MolaGPT.Core.Memory;
using MolaGPT.Core.Models;

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
    private readonly IMemoryToolBackend? _memory;
    private readonly AgentTaskRegistry? _tasks;
    private readonly ISubagentRunner? _subagents;

    /// <summary>Each turn's tool calls so far, which automatic review reads as
    /// background. Keyed by the turn's request, so a record goes with its turn.</summary>
    private readonly ConditionalWeakTable<ChatRequest, TurnCalls> _turnCalls = new();

    public ChatToolHost(
        McpClientManager mcp,
        VisionProxyTool vision,
        ImageAnalysisTool imageAnalysis,
        ImageGenerationTool imageGeneration,
        PythonExecutionTool python,
        BrowserControlTool browser,
        IToolApprovalService? approval = null,
        BrowserActivityLog? browserActivity = null,
        IMemoryToolBackend? memory = null,
        AgentTaskRegistry? tasks = null,
        ISubagentRunner? subagents = null)
    {
        _memory = memory;
        _tasks = tasks;
        _subagents = subagents;
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

        var backgroundTasks = options.BackgroundTasks && _tasks is not null;
        if (options.Python?.Enabled == true)
            tools.Add(PythonExecutionTool.BuildOpenAiToolDefinition(options.Python, backgroundTasks));

        if (options.Browser?.Enabled == true)
            tools.Add(BrowserControlTool.BuildOpenAiToolDefinition(options.Browser));

        // A sub-agent is offered exactly what its parent was, down to these, so the
        // two requests share a prefix. Its limits are enforced in ExecuteAsync.
        var subagents = options.Subagents && _subagents is not null && _tasks is not null;
        if (subagents)
        {
            tools.Add(SubagentTool.BuildDefinition());
            tools.Add(SubagentTool.BuildSendDefinition());
            tools.Add(SubagentTool.BuildFollowupDefinition());
            tools.Add(SubagentTool.BuildWaitDefinition());
        }
        if (backgroundTasks || subagents)
        {
            tools.Add(TaskTools.BuildStatusDefinition());
            tools.Add(TaskTools.BuildStopDefinition());
        }

        // Two switches, two tools: 使用记忆 owns the write side, 回忆对话 owns the
        // search side. They are independent because searching past chats sends
        // conversation text to the API service while writing a memory does not.
        if (_memory is not null && options.Memory) tools.Add(MemoryTools.BuildWriteDefinition());
        if (_memory is not null && (options.Memory || options.MemoryRecall))
            tools.Add(MemoryTools.BuildRecallDefinition());

        // An unreachable server leaves its tools out of this turn rather than failing
        // it; the manager reports the server as unavailable.
        foreach (var server in EnabledMcpServers(options))
        {
            if (await _mcp.TryGetToolsAsync(server, ct).ConfigureAwait(false) is not { } listing) continue;
            tools.AddRange(listing.Tools.Select(tool => BuildMcpToolDefinition(server, tool)));
        }

        return tools;
    }

    private static IEnumerable<McpServerOptions> EnabledMcpServers(LocalToolOptions options) =>
        options.McpServers?.Where(s => s.Enabled) ?? Enumerable.Empty<McpServerOptions>();

    private static object BuildMcpToolDefinition(McpServerOptions server, McpToolDescriptor tool) => new
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
                : throw new InvalidDataException($"MCP 工具 {tool.Name} 的参数定义不是对象。")
        }
    };

    public Task<IReadOnlyDictionary<string, AgentToolHints>> DescribeAgentToolsAsync(
        LocalToolOptions options,
        CancellationToken ct)
    {
        var hints = new Dictionary<string, AgentToolHints>(StringComparer.Ordinal);
        foreach (var server in EnabledMcpServers(options))
        {
            // Listed moments ago for this turn's catalogue. A server that was not
            // has no tools in it, and has already been reported.
            if (_mcp.ListedTools(server) is not { } listing) continue;
            var ns = new AgentToolNamespace(
                McpToolName.Prefix + McpToolName.Slugify(server.Id),
                string.IsNullOrWhiteSpace(listing.Instructions)
                    ? $"MCP 服务器「{server.Name}」"
                    : $"MCP 服务器「{server.Name}」\n{listing.Instructions.Trim()}");
            foreach (var tool in listing.Tools)
            {
                hints[McpToolName.Build(server.Id, tool.Name)] = new AgentToolHints(
                    McpExposures.Normalize(server.Exposure),
                    ns,
                    tool.Hints,
                    McpClientManager.ResultSchema(tool.OutputSchema));
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, AgentToolHints>>(hints);
    }

    public async Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        options = WithConversationWorkspace(options, context);
        var earlierCalls = _turnCalls.GetValue(context.Request, _ => new TurnCalls()).Add(toolName, argumentsJson);

        // A sub-agent sees its parent's whole tool list, so these arrive as ordinary
        // calls and are turned away here.
        if (options.IsSubagent && SubagentRefusal(toolName) is { } blocked)
            return ToolError(blocked);

        if (toolName is "search_web" or "web_fetch" or "read_file" or "glob_files" or "grep_files")
        {
            var request = WithWorkspaceScope(
                ToolCapabilityCatalog.ForBuiltIn(toolName, argumentsJson), toolName, argumentsJson, options);
            if (!await IsApprovedAsync(request, options.PermissionMode, options.IsSubagent, ct).ConfigureAwait(false))
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
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.VisionPermissionMode), options.IsSubagent, ct).ConfigureAwait(false))
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

            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.VisionPermissionMode), options.IsSubagent, ct).ConfigureAwait(false))
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
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.ImageGenerationPermissionMode), options.IsSubagent, ct).ConfigureAwait(false))
                return PermissionDenied(toolName);
            return await _imageGeneration.ExecuteToolAsync(
                argumentsJson, options.ImageGeneration, options.WorkspaceRoot, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, PythonExecutionTool.ToolName, StringComparison.Ordinal))
        {
            var workspaceConversation = WorkspaceConversation(options, context);
            var run = new PythonRunContext(
                string.IsNullOrWhiteSpace(workspaceConversation)
                    ? null
                    : new AgentTaskOwner(workspaceConversation!, context.Request.SessionId),
                AllowBackground: options.BackgroundTasks && !options.IsSubagent,
                Unattended: options.IsSubagent,
                Review: PythonReviewContext.From(context.Request, earlierCalls) with { ReportActivity = context.ReportActivity });
            return await _python.ExecuteAsync(argumentsJson, options.Python, workspaceConversation, run, ct).ConfigureAwait(false);
        }

        if (toolName is TaskTools.StatusToolName or TaskTools.StopToolName)
        {
            var conversation = WorkspaceConversation(options, context);
            if (_tasks is null || string.IsNullOrWhiteSpace(conversation))
                return ToolError("后台任务不可用。");
            return toolName == TaskTools.StatusToolName
                ? await TaskTools.ExecuteStatusAsync(_tasks, conversation!, argumentsJson, ct).ConfigureAwait(false)
                : TaskTools.ExecuteStop(_tasks, conversation!, argumentsJson);
        }

        if (string.Equals(toolName, SubagentTool.ToolName, StringComparison.Ordinal))
            return await SpawnSubagentAsync(argumentsJson, context, options, ct).ConfigureAwait(false);

        if (toolName == SubagentTool.SendToolName)
            return SendAgentMessage(argumentsJson, context, options);
        if (toolName == SubagentTool.FollowupToolName)
            return await FollowupAgentAsync(argumentsJson, context, options, ct).ConfigureAwait(false);
        if (toolName == SubagentTool.WaitToolName)
            return await WaitAgentsAsync(argumentsJson, context, options, ct).ConfigureAwait(false);

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
                if (!await IsApprovedAsync(request, mode, options.IsSubagent, ct).ConfigureAwait(false))
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

        if (toolName is MemoryTools.RecallToolName or MemoryTools.WriteToolName)
        {
            // Trimmed against what this turn actually offered, not against what
            // the model asked for. Models call tools they were never given —
            // hallucinated, or talked into it by a page they just read — and
            // 「关掉记忆」 has to mean the write cannot land, not merely that we
            // kept quiet about the tool.
            if (_memory is null) return ToolError("Memory is not available.");
            var allowed = toolName == MemoryTools.WriteToolName
                ? options.Memory
                : options.Memory || options.MemoryRecall;
            if (!allowed) return MemoryTools.Error("本轮未启用记忆工具。");

            return await _memory.ExecuteAsync(
                toolName,
                argumentsJson,
                context.Request.ConversationId,
                LastUserText(context.Request.Messages),
                ct).ConfigureAwait(false);
        }

        if (McpToolName.TryDecode(toolName, out var serverSlug, out var toolSlug))
            return await ExecuteMcpAsync(serverSlug, toolSlug, argumentsJson, options, ct).ConfigureAwait(false);

        return ToolError($"Unknown tool: {toolName}");
    }

    /// <summary>
    /// The turn's own user message, which is the only text a memory quote may
    /// come from. Taken here rather than trusted from the model: a tool argument
    /// saying which message it quoted would be the model vouching for itself.
    /// </summary>
    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatMessage.RoleUser) continue;
            var text = messages[i].AsText();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
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
            if (!await IsApprovedAsync(request, EffectiveMode(options.PermissionMode, options.McpPermissionMode), options.IsSubagent, ct).ConfigureAwait(false))
                return PermissionDenied(request.ToolName);

            return await _mcp.CallToolAsync(server, descriptor, argumentsJson, ct).ConfigureAwait(false);
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

    /// <summary>The conversation whose working directory this turn's tools use: the
    /// request's own, except for a sub-agent, which works in its parent's.</summary>
    private static string? WorkspaceConversation(LocalToolOptions options, ChatToolContext context) =>
        string.IsNullOrWhiteSpace(options.WorkspaceConversationId)
            ? context.Request.ConversationId
            : options.WorkspaceConversationId;

    private static LocalToolOptions WithConversationWorkspace(LocalToolOptions options, ChatToolContext context)
    {
        var conversation = WorkspaceConversation(options, context);
        if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot)
            || string.IsNullOrWhiteSpace(conversation))
            return options;

        var workspace = PythonExecutionTool.GetSessionDirectory(conversation);
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

    /// <param name="unattended">A sub-agent is asking: nobody is watching its turn, so
    /// a dialog would appear out of nowhere. What would prompt is refused; what the
    /// user's mode already lets through without a dialog still goes.</param>
    private async Task<bool> IsApprovedAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        bool unattended,
        CancellationToken ct)
    {
        if (unattended && mode == ToolPermissionMode.FullAccess)
            return !request.AlwaysAsk;

        if (_approval is null || unattended)
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
    }, ToolJson);

    private static readonly JsonSerializerOptions ToolJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>What a sub-agent may not do, and why, or null when the call may run.</summary>
    private static string? SubagentRefusal(string toolName) => toolName switch
    {
        BrowserControlTool.ToolName => "子 Agent 不能操作浏览器。",
        MemoryTools.WriteToolName => "子 Agent 不能写入记忆。",
        SubagentTool.ToolName => "子 Agent 不能再创建子 Agent。",
        TaskTools.StopToolName => "子 Agent 不能停止后台任务。",
        SubagentTool.FollowupToolName => "子 Agent 不能给其他子 Agent 派活。",
        _ => null
    };

    /// <summary>
    /// Hand a task to a sub-agent. In the background by default: the task registry
    /// owns the run, and its end reaches the parent as a notification. In the
    /// foreground the call waits, and stopping the turn stops the sub-agent with it.
    /// </summary>
    private async Task<string> SpawnSubagentAsync(
        string argumentsJson,
        ChatToolContext context,
        LocalToolOptions options,
        CancellationToken ct)
    {
        if (_subagents is null || _tasks is null || !options.Subagents)
            return ToolError("子 Agent 不可用。");
        var conversation = context.Request.ConversationId;
        if (string.IsNullOrWhiteSpace(conversation))
            return ToolError("子 Agent 只能在对话中使用。");
        if (SubagentTool.Parse(argumentsJson) is not { } args)
            return ToolError("缺少任务说明（task）。");
        SubagentModel model;
        try { model = _subagents.ResolveModel(context); }
        catch (InvalidOperationException ex) { return ToolError(ex.Message); }
        if (args.InheritContext && model.ProviderId != context.ProviderId)
            return ToolError("当前子 Agent 模型与主 Agent 使用不同服务，无法继承完整上下文。请使用 context=none。");
        if (!_tasks.TryReserve(
                new AgentTaskOwner(conversation!, context.Request.SessionId),
                AgentTaskKinds.Agent,
                out var reservation,
                out var full))
            return ToolError(full!);

        using (reservation!)
        {
            var agent = _tasks.RegisterAgent(conversation!, args.Label, model.ProviderId, model.ModelId);
            var request = new SubagentRequest(context, agent, args.Task, args.InheritContext, FirstRun: true);
            var task = StartAgentTurn(reservation!, agent, request);
            if (!args.Background)
            {
                await WaitForTaskAsync(task, ct).ConfigureAwait(false);
                _tasks.MarkDelivered(task);
                return JsonSerializer.Serialize(new { success = task.Status == AgentTaskStatus.Completed,
                    agent_id = agent.Id, provider_id = agent.ProviderId, model = agent.ModelId,
                    label = agent.Label, answer = task.Report }, ToolJson);
            }
            return JsonSerializer.Serialize(new
            {
                success = true,
                background = true,
                task_id = task.Id,
                agent_id = agent.Id,
                provider_id = agent.ProviderId,
                model = agent.ModelId,
                status = "running",
                label = args.Label,
                note = "子 Agent 已在后台运行。结束时会自动通知你。"
            }, ToolJson);
        }
    }

    private AgentTask StartAgentTurn(AgentTaskReservation reservation, AgentHandle agent, SubagentRequest request) =>
        _tasks!.Start(reservation, agent.Label, async (_, taskCt) =>
        {
            var result = await _subagents!.RunAsync(request, taskCt).ConfigureAwait(false);
            return new AgentTaskOutcome(result.Success, result.Answer);
        }, () => agent.RecentOutput, agentId: agent.Id);

    private async Task WaitForTaskAsync(AgentTask task, CancellationToken ct)
    {
        try
        {
            while (task.IsRunning)
            {
                var signal = _tasks!.CaptureChangeSignal();
                if (!task.IsRunning) break;
                await signal.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _tasks!.Stop(task.ConversationId, task.Id);
            throw;
        }
    }

    private string SendAgentMessage(string argumentsJson, ChatToolContext context, LocalToolOptions options)
    {
        if (_tasks is null) return ToolError("子 Agent 不可用。");
        var conversation = WorkspaceConversation(options, context);
        if (string.IsNullOrWhiteSpace(conversation)) return ToolError("当前对话不可用。");
        using var doc = ParseToolArguments(argumentsJson);
        if (doc is null) return ToolError("消息参数无效。");
        var target = ReadToolString(doc.RootElement, "agent_id");
        var message = ReadToolString(doc.RootElement, "message");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(message))
            return ToolError("缺少 agent_id 或 message。");
        var sender = options.IsSubagent ? options.AgentId : "parent";
        if (sender is null) return ToolError("子 Agent 身份无效。");
        var sent = target == "parent"
            ? options.IsSubagent && _tasks.QueueToParent(conversation!, sender, message!)
            : _tasks.QueueToAgent(conversation!, target!, sender, message!);
        return sent
            ? JsonSerializer.Serialize(new { success = true, agent_id = target, status = "queued" }, ToolJson)
            : ToolError("收件方不在本对话中。");
    }

    private async Task<string> FollowupAgentAsync(
        string argumentsJson, ChatToolContext context, LocalToolOptions options, CancellationToken ct)
    {
        if (_tasks is null || _subagents is null || !options.Subagents)
            return ToolError("子 Agent 不可用。");
        var conversation = context.Request.ConversationId;
        if (string.IsNullOrWhiteSpace(conversation)) return ToolError("当前对话不可用。");
        using var doc = ParseToolArguments(argumentsJson);
        if (doc is null) return ToolError("派活参数无效。");
        var agentId = ReadToolString(doc.RootElement, "agent_id");
        var text = ReadToolString(doc.RootElement, "task");
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(text))
            return ToolError("缺少 agent_id 或 task。");
        var agent = _tasks.FindAgent(conversation!, agentId!);
        if (agent is null) return ToolError("本对话没有这个子 Agent。");
        if (!_tasks.TryReserveAgentTurn(new AgentTaskOwner(conversation!, context.Request.SessionId),
                agent.Id, out var reservation, out var error))
            return ToolError(error!);
        using (reservation!)
        {
            var request = new SubagentRequest(context, agent, text!, InheritContext: false, FirstRun: false);
            var task = StartAgentTurn(reservation!, agent, request);
            var background = !doc.RootElement.TryGetProperty("run_in_background", out var flag)
                             || flag.ValueKind != JsonValueKind.False;
            if (!background)
            {
                await WaitForTaskAsync(task, ct).ConfigureAwait(false);
                _tasks.MarkDelivered(task);
                return JsonSerializer.Serialize(new { success = task.Status == AgentTaskStatus.Completed,
                    agent_id = agent.Id, provider_id = agent.ProviderId, model = agent.ModelId,
                    label = agent.Label, answer = task.Report }, ToolJson);
            }
            return JsonSerializer.Serialize(new { success = true, background = true,
                agent_id = agent.Id, task_id = task.Id, label = agent.Label,
                provider_id = agent.ProviderId, model = agent.ModelId,
                status = "running" }, ToolJson);
        }
    }

    private async Task<string> WaitAgentsAsync(
        string argumentsJson, ChatToolContext context, LocalToolOptions options, CancellationToken ct)
    {
        if (_tasks is null) return ToolError("子 Agent 不可用。");
        var conversation = WorkspaceConversation(options, context);
        if (string.IsNullOrWhiteSpace(conversation)) return ToolError("当前对话不可用。");
        using var doc = ParseToolArguments(argumentsJson);
        if (doc is null || !doc.RootElement.TryGetProperty("agent_ids", out var idsElement)
            || idsElement.ValueKind != JsonValueKind.Array)
            return ToolError("缺少 agent_ids。");
        var ids = idsElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || ids.Length > 8) return ToolError("agent_ids 数量应为 1 到 8。");
        var agents = ids.Select(id => _tasks.FindAgent(conversation!, id)).ToArray();
        if (agents.Any(a => a is null))
            return ToolError("有子 Agent 不在当前任务列表中，无法等待；这些 ID 当前没有运行中的任务。");
        var timeout = doc.RootElement.TryGetProperty("timeout_seconds", out var seconds)
                      && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var value)
            ? Math.Clamp(value, 1, 60) : 30;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var signal = _tasks.CaptureChangeSignal();
            var states = agents.Select(a => DescribeAgent(_tasks, a!)).ToArray();
            var inbox = options.IsSubagent && options.AgentId is { } agentId
                ? _tasks.PeekAgentMessages(conversation!, agentId)
                : _tasks.PeekParentMessages(conversation!);
            var hasMessage = inbox
                .Any(m => ids.Contains(m.AgentId, StringComparer.Ordinal));
            if (states.Any(s => s.status != "running") || hasMessage)
            {
                foreach (var state in states)
                {
                    if (state.task_id is { } taskId && state.status != "running"
                        && _tasks.Find(conversation!, taskId) is { IsRunning: false } task)
                        _tasks.MarkDelivered(task);
                }
                return JsonSerializer.Serialize(new { agents = states, has_message = hasMessage }, ToolJson);
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return JsonSerializer.Serialize(new { agents = states, has_message = false, timed_out = true }, ToolJson);
            if (!await AgentTaskRegistry.WaitForChangeAsync(signal, remaining, ct).ConfigureAwait(false))
                return JsonSerializer.Serialize(new { agents = states, has_message = false, timed_out = true }, ToolJson);
        }
    }

    private sealed record AgentStatus(string agent_id, string label, string model, string status,
        string? task_id, string? recent_output, string? report);

    private static AgentStatus
        DescribeAgent(AgentTaskRegistry registry, AgentHandle agent)
    {
        var task = agent.LatestTaskId is { } id ? registry.Find(agent.ConversationId, id) : null;
        return new AgentStatus(agent.Id, agent.Label, agent.ModelId,
            task is null ? "idle" : TaskTools.StatusName(task.Status),
            task?.Id, task?.IsRunning == true ? task.Tail() : null,
            task?.IsRunning == false ? task.Report : null);
    }

    private static JsonDocument? ParseToolArguments(string json)
    {
        try
        {
            var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadToolString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    /// <summary>Global FullAccess overrides per-tool; per-tool FullAccess overrides global Approval.</summary>
    private static ToolPermissionMode EffectiveMode(ToolPermissionMode global, ToolPermissionMode perTool) =>
        global == ToolPermissionMode.FullAccess || perTool == ToolPermissionMode.FullAccess
            ? ToolPermissionMode.FullAccess
            : ToolPermissionMode.Approval;

    /// <summary>The latest calls of one turn, arguments clipped, oldest dropped.
    /// Parallel calls arrive on different threads.</summary>
    private sealed class TurnCalls
    {
        private const int Max = 12;
        private const int MaxArgumentChars = 2000;
        private readonly List<ReviewedToolCall> _calls = [];

        /// <summary>Records a call and returns the ones before it.</summary>
        public IReadOnlyList<ReviewedToolCall> Add(string tool, string arguments)
        {
            lock (_calls)
            {
                var earlier = _calls.ToArray();
                _calls.Add(new ReviewedToolCall(tool,
                    arguments.Length <= MaxArgumentChars ? arguments : arguments[..MaxArgumentChars] + "…"));
                if (_calls.Count > Max) _calls.RemoveAt(0);
                return earlier;
            }
        }
    }
}
