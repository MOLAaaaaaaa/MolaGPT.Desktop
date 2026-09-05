using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// The chat engine for <b>Work</b> and every BYOK provider — there is no second
/// one. Pi owns the agent loop, context compaction and retry; MolaGPT keeps the
/// billing endpoint (account quota / BYOK), the sandboxed tools and the approval
/// flow. The LLM stream comes back over JSONL RPC and is translated to
/// <see cref="ChatChunk"/> so the chat UI consumes it unchanged.
///
/// This type is thin on purpose: it shapes one turn and hands it to
/// <see cref="PiRuntime"/>, which owns the processes. Only <c>molagpt-proxy</c>
/// (Chat mode) bypasses it, because Chat has no agent loop to run.
/// </summary>
public sealed class PiWorkProvider : IChatProvider, IStatefulHistoryProvider, IOneShotTarget, IAsyncDisposable
{
    /// <summary>Provider id the sidecar extension registers (must match the value
    /// passed on the <c>pi --provider</c> flag and to <c>set_model</c>).</summary>
    public const string SidecarProviderId = "molagpt-work";

    private readonly PiWorkProviderConfig _config;
    private readonly IChatToolHost _toolHost;
    private readonly HttpClient _http;
    private readonly PiRuntime _runtime;
    private readonly Action<string>? _log;

    /// <summary>How long to keep retrying the transcript rewrite while the sidecar
    /// we just evicted still holds the file open.</summary>
    private const int FileRetryLimit = 5;
    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromMilliseconds(120);

    public PiWorkProvider(
        PiWorkProviderConfig config,
        IChatToolHost toolHost,
        HttpClient http,
        PiRuntime runtime,
        Action<string>? log = null)
    {
        _config = config;
        _toolHost = toolHost;
        _http = http;
        _runtime = runtime;
        _log = log;
    }

    public string Id => _config.ProviderId;
    public string DisplayName => _config.DisplayName;
    public ProviderKind Kind => ProviderKind.MolaGptLocalTools;
    public IReadOnlyList<ProviderModel> Models => _config.Models;

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var latestUser = LatestUserMessage(request.Messages);
        var userText = latestUser?.AsText() ?? string.Empty;
        var images = ExtractImages(latestUser);
        var files = ExtractFiles(latestUser);
        if (string.IsNullOrWhiteSpace(userText) && images.Count == 0 && files.Count == 0) yield break;

        var options = LocalToolOptions.FromExtraBody(request.ExtraBody);
        var modelSupportsVision = Models.FirstOrDefault(m => m.Id == request.ModelId)?.SupportsVision ?? false;

        // Pi launches with --no-builtin-tools, so its only file access is the
        // bridged MolaGPT tool set. read_file / execute_python_code both resolve
        // relative paths against this conversation's workspace — the same folder
        // the composer copied the attachments into — so the `path` values in this
        // section are directly usable by the agent.
        var fileSection = AttachedFilePrompt.Build(files, AttachmentPromptOptions.From(options, modelSupportsTools: true));
        if (!string.IsNullOrWhiteSpace(fileSection))
        {
            userText = string.IsNullOrWhiteSpace(userText)
                ? fileSection!
                : userText.TrimEnd() + "\n\n" + fileSection;
        }

        // LocalHttpClient must be supplied: ChatToolHost refuses search_web /
        // web_fetch / read_file / glob_files / grep_files without it.
        var toolContext = new ChatToolContext(request, Id, request.ModelId, modelSupportsVision, Models, _http);

        // MolaGPT's live tool set for this turn, in the same order the direct
        // provider sends it. The sidecar registers exactly these, so names and
        // schemas can never drift from what ChatToolHost dispatches on.
        var localDefs = LocalToolRegistry.BuildOpenAiToolDefinitions(options);
        var hostDefs = await _toolHost.BuildToolDefinitionsAsync(toolContext, options, ct).ConfigureAwait(false);
        var toolCatalogJson = JsonSerializer.Serialize(localDefs.Concat(hostDefs).ToArray());

        var creds = _config.ResolveCreds(request);

        // Reasoning is asked for twice, on purpose, because neither half covers the
        // other:
        //
        //  · Pi's thinking level is what its own loop reads, and the only way to
        //    reach a nested config like Google's generationConfig.thinkingConfig —
        //    a merged body only ever lands top-level keys.
        //  · The body merge carries the dialect the user picked per model
        //    (ThinkingParamKind). Pi cannot infer that: the shim hides the real
        //    endpoint from its compatibility detection, so it would fall back to
        //    plain reasoning_effort and a model configured for Qwen's
        //    enable_thinking + budget would quietly stop honouring the setting.
        var thinkingLevel = ResolveThinkingLevel(request, creds.Api);
        if (TakesOpenAiThinkingDialect(creds.Api))
            creds = creds with { ExtraBody = MergeThinking(creds.ExtraBody, request) };

        // Personas and per-model prompts arrive as system messages. Pi substitutes
        // its own coding-assistant prompt when nobody says otherwise, so failing to
        // forward these would quietly discard whatever the user selected.
        var systemPrompt = ExtractSystemPrompt(request.Messages);

        // The tool set is deliberately NOT part of the sidecar's identity: the
        // extension re-reads this catalogue every turn (before_agent_start) and
        // reconciles Pi's tools in place, so toggling 联网搜索 / 视觉 / MCP costs a
        // catalogue fetch rather than a respawn.
        var binding = new PiWorkToolBridge.TurnBinding(
            (name, argsJson, toolCt) => _toolHost.ExecuteAsync(name, argsJson, toolContext, options, toolCt),
            () => toolCatalogJson,
            () => systemPrompt);

        var target = new PiWorkLlmShim.ForwardTarget(
            creds.Endpoint,
            creds.TokenProvider,
            creds.OnUnauthorized,
            creds.Headers,
            creds.ExtraBody,
            creds.Auth,
            creds.DropBodyKeys ?? PiEndpointQuirks.DropBodyKeysFor(creds.Endpoint),
            creds.PathMode);

        // Waits when every slot is busy, which is the point: three turns really are
        // in flight and a fourth process costs more than the wait.
        await using var lease = await _runtime.AcquireAsync(
            _config.Spec,
            request.ConversationId ?? PiRuntime.DraftKey,
            target,
            binding,
            ct).ConfigureAwait(false);

        // A cold sidecar costs a Node boot (~2.7s measured) before the model is even
        // asked anything. Saying so beats a generic "等待模型回答" that makes the wait
        // look like the model being slow.
        if (!lease.WasWarm)
        {
            yield return new ChatChunk(Pending: new PendingStatusDelta(
                "启动 Agent 运行时", "正在拉起本地 Agent 运行时"));
        }

        string? errorMessage = null;
        var pendingArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        var preview = new ToolPreviewTracker();

        // The same window Pi was catalogued with, so the gauge is measured against
        // the very number its auto-compaction is thresholded on.
        var contextWindow = ModelContextWindows.ResolveOrDefault(
            creds.Model,
            Models.FirstOrDefault(m => m.Id.Equals(creds.Model, StringComparison.OrdinalIgnoreCase))?.ContextWindow);

        await foreach (var line in lease.Session
                           .SendTurnAsync(creds.Model, thinkingLevel, userText, images, ct)
                           .ConfigureAwait(false))
        {
            var chunk = MapLine(line, options, pendingArgs, preview, contextWindow, ref errorMessage);
            if (chunk is not null) yield return chunk;
        }

        if (errorMessage is not null)
            throw new InvalidOperationException(HumanizeAgentError(errorMessage));
    }

    /// <summary>
    /// Summarize a conversation's history on demand, ahead of the threshold.
    ///
    /// Worth doing early: a summary made at 60% is written from a complete view of
    /// the conversation, while one forced at the limit is written from a view that
    /// is already crowded — the agent compacts either way, this only decides whether
    /// the user picks the moment.
    ///
    /// Runs through a full lease because the summary is a real model call and has to
    /// bill to the same endpoint the conversation does. It is the only work in the
    /// slot while it holds it, which is correct: compacting beside a live answer
    /// would have two writers rewriting one transcript.
    /// </summary>
    public async Task<PiCompactionResult?> CompactAsync(
        string? conversationId,
        string modelId,
        string? customInstructions,
        CancellationToken ct)
    {
        await using var lease = await AcquireOperationLeaseAsync(conversationId, modelId, ct)
            .ConfigureAwait(false);
        return await lease.Session.CompactAsync(modelId, customInstructions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The user's standing answer to "may the agent summarize my history on its
    /// own?". Application-wide and owned by <see cref="PiRuntime"/>, which re-applies
    /// it to every sidecar it hands out — see the property there for why it cannot
    /// live any closer to the session.
    /// </summary>
    public bool AutoCompactionEnabled
    {
        get => _runtime.AutoCompactionEnabled;
        set => _runtime.AutoCompactionEnabled = value;
    }

    /// <summary>
    /// Switch Pi's automatic compaction, now and for every sidecar afterwards.
    ///
    /// Turning it off means the transcript is never summarized behind the user's
    /// back, and equally that a long enough conversation will eventually be refused
    /// upstream. That trade is the user's to make, which is the whole reason this is
    /// reachable.
    ///
    /// Sent explicitly even though the lease already re-applied it, because that
    /// re-apply is deliberately best-effort — it must never fail a turn. A click is
    /// the one moment where a sidecar that refuses the command has to be reported
    /// rather than swallowed, so this path keeps its own throwing round-trip.
    /// </summary>
    public async Task SetAutoCompactionAsync(
        string? conversationId,
        string modelId,
        bool enabled,
        CancellationToken ct)
    {
        AutoCompactionEnabled = enabled;
        await using var lease = await AcquireOperationLeaseAsync(conversationId, modelId, ct)
            .ConfigureAwait(false);
        await lease.Session.SetAutoCompactionAsync(enabled, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A lease for work that is not a turn: same endpoint and same transcript, but
    /// no tools and no system prompt.
    ///
    /// The empty tool catalogue is safe because the bridge binding is per-lease —
    /// the next real turn installs the full set again — and compaction never calls a
    /// tool anyway. The executor throws rather than returning something bland, so a
    /// future caller that does need tools finds out immediately instead of getting
    /// silently toolless behaviour.
    /// </summary>
    private async Task<PiTurnLease> AcquireOperationLeaseAsync(
        string? conversationId,
        string modelId,
        CancellationToken ct)
    {
        var creds = _config.ResolveCreds(new ChatRequest(modelId, Array.Empty<ChatMessage>()));

        var binding = new PiWorkToolBridge.TurnBinding(
            (name, _, _) => throw new InvalidOperationException(
                $"Pi 在非对话操作中请求了工具 {name}，该通道不提供工具。"),
            () => "[]",
            () => null);

        var target = new PiWorkLlmShim.ForwardTarget(
            creds.Endpoint,
            creds.TokenProvider,
            creds.OnUnauthorized,
            creds.Headers,
            creds.ExtraBody,
            creds.Auth,
            creds.DropBodyKeys ?? PiEndpointQuirks.DropBodyKeysFor(creds.Endpoint),
            creds.PathMode);

        return await _runtime.AcquireAsync(
            _config.Spec,
            conversationId ?? PiRuntime.DraftKey,
            target,
            binding,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pi reports an upstream failure as <c>"&lt;status&gt;: &lt;raw body&gt;"</c>,
    /// which puts a wall of JSON in front of the one sentence that says what to do
    /// about it. This peels it back to the human message and names the rate limit
    /// explicitly — 429 is what a long research turn actually dies of, and the bare
    /// status code reads like a crash instead of "you went too fast".
    /// </summary>
    internal static string HumanizeAgentError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Agent 运行失败。";

        var text = raw.Trim();
        var status = 0;
        var separator = text.IndexOf(':');
        if (separator > 0
            && int.TryParse(text[..separator], out var parsed)
            && parsed is >= 100 and <= 599)
        {
            status = parsed;
            text = text[(separator + 1)..].Trim();
        }

        var detail = ChatApiErrorHelper.ExtractErrorMessage(text);
        if (string.IsNullOrWhiteSpace(detail)) detail = text;

        return status switch
        {
            0 => detail,
            429 => $"请求过于频繁，请稍后重试（429）。{detail}",
            _ => $"HTTP {status}：{detail}"
        };
    }

    /// <summary>
    /// Hand out this provider's upstream target so a one-shot call (conversation
    /// title, vision lookup) can reach the same endpoint without paying for a
    /// sidecar. The creds resolver is the single source of truth for where a turn
    /// bills to, so a one-shot answer can never come from a different endpoint
    /// than the conversation it belongs to.
    /// </summary>
    public OneShotTarget? DescribeOneShot(string modelId)
    {
        if (Models.All(m => !m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
            return null;

        var creds = _config.ResolveCreds(new ChatRequest(modelId, Array.Empty<ChatMessage>()));
        var endpoint = creds.Api == "google-generative-ai"
            ? $"{creds.Endpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(modelId)}:generateContent"
            : creds.Endpoint;
        return new OneShotTarget(
            endpoint,
            creds.TokenProvider,
            DisplayName,
            creds.Api switch
            {
                "anthropic-messages" => OneShotWireApi.AnthropicMessages,
                "openai-responses" => OneShotWireApi.OpenAiResponses,
                "google-generative-ai" => OneShotWireApi.GoogleGenerativeAi,
                _ => OneShotWireApi.OpenAiCompletions,
            },
            creds.Headers,
            creds.ExtraBody);
    }

    /// <summary>
    /// Forget the newest exchange so the retry that follows regenerates it.
    ///
    /// Pi owns the transcript, so the composer trimming its own message list buys
    /// nothing here: without this, a retry arrives as an ordinary next turn and the
    /// model answers it with the attempt being replaced — and that attempt's tool
    /// results — still in view.
    ///
    /// Both copies of the turn have to go. A sidecar holds it in memory and the
    /// next turn resumes from the file, so trimming one without the other just
    /// moves the problem.
    /// </summary>
    public async Task<bool> ForgetLastTurnAsync(string? conversationId, CancellationToken ct = default)
    {
        var key = conversationId ?? PiRuntime.DraftKey;

        // Evict before rewriting: a process still holding this transcript would
        // keep its own in-memory copy of the turn being erased, and write it back.
        await _runtime.EvictConversationAsync(key).ConfigureAwait(false);

        var file = PiRuntime.ResolveSessionPath(_config.Spec.SessionRoot, key);
        if (!File.Exists(file)) return false;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                var keep = PiSessionRewind.KeepCountBeforeLastUserTurn(lines);
                if (keep < 0) return false;

                var trimmed = new StringBuilder();
                for (var i = 0; i < keep; i++) trimmed.Append(lines[i]).Append('\n');
                await File.WriteAllTextAsync(file, trimmed.ToString(), ct).ConfigureAwait(false);

                _log?.Invoke($"[pi-work] 回退最后一轮：{key}（{lines.Length} → {keep} 行）");
                return true;
            }
            // Kill() returns before Windows has actually torn the process down, so
            // the first read can still meet the sidecar's own handle.
            catch (IOException) when (attempt < FileRetryLimit)
            {
                await Task.Delay(FileRetryDelay, ct).ConfigureAwait(false);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    private ChatChunk? MapLine(
        string line,
        LocalToolOptions options,
        IDictionary<string, string> pendingArgs,
        ToolPreviewTracker preview,
        int contextWindow,
        ref string? errorMessage)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "message_update":
                    if (root.TryGetProperty("assistantMessageEvent", out var ev))
                    {
                        var kind = ev.TryGetProperty("type", out var k) ? k.GetString() : null;
                        var delta = ev.TryGetProperty("delta", out var d) ? d.GetString() : null;
                        if (kind == "text_delta" && delta is not null)
                            return new ChatChunk(DeltaText: delta);
                        if (kind == "thinking_delta" && delta is not null)
                            return new ChatChunk(DeltaThinking: delta);

                        // Tool arguments stream in before the call runs. Writing a
                        // long script can take a while, and without these the UI
                        // showed nothing at all until execution began — the model
                        // looked hung when it was busy typing.
                        if (kind is "toolcall_start" or "toolcall_delta")
                            return BuildPreparingCard(ev, options, preview);

                        // The finished block is where the call's id is certain to be
                        // present, whatever order the model streamed it in. It is what
                        // ties the preview card to the execution events below.
                        if (kind == "toolcall_end")
                            NoteFinishedToolCall(ev, preview);
                    }
                    return null;

                case "tool_execution_start":
                {
                    var startId = Str(root, "toolCallId");
                    var startArgs = root.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";

                    // Pi reports the arguments when a call starts but not when it
                    // ends, and the finished card needs them — hold on to them.
                    pendingArgs[startId] = startArgs;
                    return new ChatChunk(Tool: ToolDeltaBuilder.BuildToolDelta(
                        preview.CardIdFor(startId), Str(root, "toolName"), startArgs, options, "running"));
                }

                case "tool_execution_end":
                {
                    var isError = root.TryGetProperty("isError", out var er) && er.GetBoolean();
                    var endId = Str(root, "toolCallId");
                    var resultJson = root.TryGetProperty("result", out var r) ? UnwrapToolResult(r) : null;
                    pendingArgs.Remove(endId, out var endArgs);

                    // Built by the same function the direct provider uses, so the
                    // duration, exit code and permission labels that live inside the
                    // tool's own result reach the card here too.
                    var delta = ToolDeltaBuilder.BuildToolDelta(
                        preview.CardIdFor(endId),
                        Str(root, "toolName"),
                        endArgs ?? "{}",
                        options,
                        isError ? "error" : "completed",
                        resultJson);

                    // MapLine's own status vocabulary is what the UI already reacts
                    // to; only the detail-building needed the "error" spelling.
                    return new ChatChunk(Tool: delta with { Status = isError ? "failed" : "completed" });
                }

                case "agent_end":
                {
                    // One agent run is many model calls, so the turn's cost is the
                    // sum rather than the last message's. Pi has been counting all
                    // along — including cache hits and reasoning tokens, which the
                    // chat-completions path never reported.
                    var input = 0;
                    var output = 0;
                    var total = 0;
                    var counted = false;

                    // The turn's cost is a sum; how full the context is, is not.
                    // Pi sizes the context from the newest assistant message alone
                    // (its token total already includes everything replayed to the
                    // model), and its compaction threshold reads that same number —
                    // so summing here would put the gauge and the trigger on
                    // different scales.
                    int? contextTokens = null;

                    if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var mm in msgs.EnumerateArray())
                        {
                            var stopReason = mm.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
                            if (stopReason == "error")
                                errorMessage = mm.TryGetProperty("errorMessage", out var em) ? em.GetString() : "Pi agent error";

                            if (!mm.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                                continue;
                            counted = true;
                            input += Int(usage, "input") + Int(usage, "cacheRead") + Int(usage, "cacheWrite");
                            output += Int(usage, "output") + Int(usage, "reasoning");
                            total += Int(usage, "totalTokens");

                            // A failed or cancelled call reports zeroes; taking those
                            // as the context size would empty the gauge exactly when
                            // the context is at its fullest, which is the reading a
                            // user is most likely to act on.
                            if (stopReason is "error" or "aborted") continue;
                            var messageTokens = Int(usage, "totalTokens");
                            if (messageTokens <= 0)
                            {
                                messageTokens = Int(usage, "input") + Int(usage, "output")
                                    + Int(usage, "cacheRead") + Int(usage, "cacheWrite");
                            }
                            if (messageTokens > 0) contextTokens = messageTokens;
                        }
                    }

                    if (!counted) return null;
                    return new ChatChunk(
                        Usage: new Usage(input, output, total),
                        ContextUsage: new ContextUsageDelta(contextTokens, contextWindow));
                }

                // Pi compacts on its own once the context nears the window. It has
                // always emitted these on the same channel as the tool events; they
                // were simply dropped here, which is why a conversation could lose
                // its own history with nothing on screen to say so.
                case "compaction_start":
                    return new ChatChunk(Compaction: new CompactionDelta(
                        Completed: false,
                        Reason: Str(root, "reason")));

                case "compaction_end":
                {
                    var aborted = root.TryGetProperty("aborted", out var ab)
                                  && ab.ValueKind == JsonValueKind.True;
                    var tokensBefore = 0;
                    var tokensAfter = 0;
                    if (root.TryGetProperty("result", out var result)
                        && result.ValueKind == JsonValueKind.Object)
                    {
                        tokensBefore = Int(result, "tokensBefore");
                        tokensAfter = Int(result, "estimatedTokensAfter");
                    }
                    return new ChatChunk(Compaction: new CompactionDelta(
                        Completed: true,
                        Reason: Str(root, "reason"),
                        TokensBefore: tokensBefore,
                        Aborted: aborted,
                        ErrorMessage: root.TryGetProperty("errorMessage", out var em)
                            ? em.GetString()
                            : null,
                        TokensAfter: tokensAfter));
                }

                case "agent_settled":
                    return new ChatChunk(FinishReason: "stop");

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Whether this wire shape understands the top-level thinking keys
    /// <see cref="ThinkingParams"/> produces (<c>reasoning_effort</c>,
    /// <c>enable_thinking</c>, <c>thinking</c>).
    ///
    /// Google's native API does not, and does not ignore them either: it validates
    /// the payload and rejects the whole request over one unknown field. There the
    /// thinking level is the only correct expression, and reaching for both is a
    /// 400 rather than a belt-and-braces.
    /// </summary>
    internal static bool TakesOpenAiThinkingDialect(string api) =>
        api is not ("google-generative-ai" or "openai-responses");

    /// <summary>
    /// Fold the turn's thinking parameters into the model's custom parameters, so
    /// the shim sends both. Custom parameters win on a clash: they are the user's
    /// explicit override.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement>? MergeThinking(
        IReadOnlyDictionary<string, JsonElement>? custom,
        ChatRequest request)
    {
        var thinking = new Dictionary<string, object?>(StringComparer.Ordinal);
        ThinkingParams.Apply(thinking, request);
        if (thinking.Count == 0) return custom;

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in thinking)
            merged[key] = JsonSerializer.SerializeToElement(value);
        if (custom is not null)
            foreach (var (key, value) in custom)
                merged[key] = value;
        return merged;
    }

    /// <summary>
    /// The turn's reasoning setting as one of Pi's thinking levels, which is what
    /// Pi's own loop reads.
    ///
    /// MolaGPT offers two levels Pi does not — <c>xhigh</c> and <c>max</c> — and Pi
    /// only accepts those from a model that declares a <c>thinkingLevelMap</c>,
    /// which ours do not. They are folded into <c>high</c> here rather than sent
    /// and silently clamped somewhere further down. Google's level-only Gemini
    /// models reject Pi's <c>off</c>/<c>minimal</c> value, so <c>low</c> is their
    /// supported floor.
    /// </summary>
    internal static string ResolveThinkingLevel(ChatRequest request, string api)
    {
        var google = api == "google-generative-ai";
        if (request.UseThinking == false) return google ? "low" : "off";

        var effort = request.ReasoningEffort?.Trim().ToLowerInvariant();
        return effort switch
        {
            "minimal" when google => "low",
            "minimal" or "low" or "medium" or "high" => effort,
            "xhigh" or "max" => "high",
            // Thinking on without a chosen effort, or a level from a vocabulary Pi
            // does not share: take its middle rather than guessing at an extreme.
            _ => request.UseThinking == true ? "medium" : "off",
        };
    }

    /// <summary>Join the turn's system messages, in order. Null when there are
    /// none, which leaves Pi on its own prompt rather than blanking it.</summary>
    private static string? ExtractSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var parts = messages
            .Where(m => m.Role == ChatMessage.RoleSystem)
            .Select(m => m.AsText())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    private static ChatMessage? LatestUserMessage(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatMessage.RoleUser)
                return messages[i];
        return null;
    }

    /// <summary>Image attachments on the turn, in Pi's wire shape. Pi owns the
    /// conversation history, so only this turn's images need sending — earlier ones
    /// are already in its session.</summary>
    private static IReadOnlyList<PiImage> ExtractImages(ChatMessage? message)
    {
        if (message?.Attachments is not { Count: > 0 } attachments) return Array.Empty<PiImage>();

        var images = new List<PiImage>();
        foreach (var attachment in attachments)
        {
            if (!attachment.IsImage || attachment.Bytes is not { Length: > 0 }) continue;
            images.Add(new PiImage(
                Convert.ToBase64String(attachment.Bytes),
                string.IsNullOrWhiteSpace(attachment.MimeType) ? "image/png" : attachment.MimeType));
        }
        return images;
    }

    /// <summary>File attachments on the turn. Like images, only this turn's are
    /// needed — Pi owns the history, and the workspace copies stay on disk, so an
    /// earlier turn's paths remain valid for the whole session.</summary>
    private static IReadOnlyList<Attachment> ExtractFiles(ChatMessage? message)
    {
        if (message?.Attachments is not { Count: > 0 } attachments) return Array.Empty<Attachment>();
        return attachments.Where(a => a.Kind == AttachmentKind.File).ToList();
    }

    /// <summary>Pi writes the session as <c>&lt;id&gt;.jsonl</c>, so the id must be a
    /// safe filename. Conversation ids are already tame; this is belt-and-braces.
    /// Public so the session-file sweeper can derive the same ids it must match.</summary>
    public static string SanitizeSessionId(string key)
    {
        var chars = key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars);
        return safe.Length <= 120 ? safe : safe[..120];
    }

    /// <summary>
    /// Pull the tool's own output out of Pi's result envelope
    /// (<c>{content:[{type:"text",text:"…"}], details:…}</c>).
    ///
    /// Showing the envelope raw is what made the tool card display a wall of
    /// escaped JSON instead of the result: the payload the direct provider puts on
    /// the card is the tool's output string, not the transport wrapper around it.
    /// </summary>
    /// <summary>
    /// Per-turn state for the "still writing the call" card. Pi streams tool
    /// arguments as deltas against a content index; the card is rebuilt from the
    /// accumulated text, throttled so a long script does not emit a chunk per token.
    /// </summary>
    internal sealed class ToolPreviewState
    {
        /// <summary>The model's own id for the call, once it has sent one.</summary>
        public string Id = "";

        /// <summary>The id the card is filed under. Equal to <see cref="Id"/> whenever
        /// the model named the call up front; otherwise minted here and kept, because
        /// changing a card's key mid-call leaves the abandoned one on screen forever.</summary>
        public string CardId = "";

        public string Name = "";
        public readonly System.Text.StringBuilder Arguments = new();
        public int LastEmittedLength;
        public long LastEmittedTicks;

        /// <summary>Set once Pi closes the block. A finished call must never take
        /// another call's deltas, however the content index is reused.</summary>
        public bool Finished;

        /// <summary>Same shape of throttle the direct provider uses: emit once a
        /// meaningful amount of new text has arrived, or after a pause.</summary>
        public bool ShouldEmit(long nowTicks) =>
            Arguments.Length - LastEmittedLength >= 120
            || nowTicks - LastEmittedTicks >= TimeSpan.TicksPerMillisecond * 250;
    }

    /// <summary>
    /// Per-turn bookkeeping for Pi's tool cards.
    ///
    /// Pi's agent loop emits one assistant message per step, and <c>contentIndex</c>
    /// numbers blocks <em>within</em> a message — so the tool call in step 2 arrives
    /// at the same index step 1's did. Filing live previews under that index alone
    /// made every later step land on the first step's card: a single card, its name
    /// frozen at whatever ran first, its arguments and result overwritten again and
    /// again by each following call.
    ///
    /// A finished call therefore releases its index, while the id → card mapping it
    /// leaves behind lives for the whole turn — that mapping is what the execution
    /// events still need after the preview slot has been recycled.
    /// </summary>
    internal sealed class ToolPreviewTracker
    {
        private readonly Dictionary<int, ToolPreviewState> _live = new();
        private readonly Dictionary<string, string> _cardIdByCallId = new(StringComparer.Ordinal);

        /// <summary>The state for a block still being written, starting a fresh one
        /// when the slot's previous occupant has already been closed.</summary>
        public ToolPreviewState Live(int contentIndex) =>
            _live.TryGetValue(contentIndex, out var state) && !state.Finished
                ? state
                : _live[contentIndex] = new ToolPreviewState();

        /// <summary>Closes the block and files its card under the id Pi settled on,
        /// so the run/result events can find it.</summary>
        public void Finish(int contentIndex, JsonElement call)
        {
            if (!_live.TryGetValue(contentIndex, out var state)) return;

            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                state.Id = id.GetString() ?? state.Id;
            if (state.Name.Length == 0
                && call.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                state.Name = name.GetString() ?? "";

            state.Finished = true;
            if (state.Id.Length > 0 && state.CardId.Length > 0)
                _cardIdByCallId[state.Id] = state.CardId;
        }

        /// <summary>The card key for a running/finished call: the key the preview
        /// already put on screen, otherwise Pi's own id. The two are identical in the
        /// common case, where the model named the call before streaming its
        /// arguments.</summary>
        public string CardIdFor(string toolCallId) =>
            _cardIdByCallId.TryGetValue(toolCallId, out var cardId) ? cardId : toolCallId;
    }

    private static ChatChunk? BuildPreparingCard(
        JsonElement ev,
        LocalToolOptions options,
        ToolPreviewTracker preview)
    {
        if (!ev.TryGetProperty("contentIndex", out var idx) || idx.ValueKind != JsonValueKind.Number)
            return null;
        var index = idx.GetInt32();

        var state = preview.Live(index);

        if (ev.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
            state.Arguments.Append(d.GetString());

        // The id and name are on the accumulated message, not on the delta — and not
        // necessarily on the first one. contentIndex is the block's own position, so
        // read exactly that block: scanning them all picks up a sibling call's id when
        // the model makes two in one message.
        if ((state.Id.Length == 0 || state.Name.Length == 0)
            && ev.TryGetProperty("partial", out var partial)
            && partial.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array
            && index < content.GetArrayLength())
        {
            var block = content[index];
            if (state.Name.Length == 0
                && block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                state.Name = n.GetString() ?? "";
            if (state.Id.Length == 0
                && block.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String)
                state.Id = i.GetString() ?? "";
        }

        // A card needs a stable key — not a known name. Waiting for both is what left
        // the UI blank for the entire time the model spent writing a long script:
        // some models announce the call before its arguments, others only name it once
        // the arguments are finished, and in the second case there was nothing on
        // screen until execution began. Take the real id when it is already there, so
        // the card is the very one the execution events go on to update; otherwise
        // mint a key, keep it, and let the label fill itself in when the name lands.
        if (state.CardId.Length == 0)
            state.CardId = state.Id.Length > 0 ? state.Id : "pi-call-" + Guid.NewGuid().ToString("N");

        var now = DateTime.UtcNow.Ticks;
        if (!state.ShouldEmit(now)) return null;
        state.LastEmittedLength = state.Arguments.Length;
        state.LastEmittedTicks = now;

        return new ChatChunk(Tool: ToolDeltaBuilder.BuildToolDelta(
            state.CardId, state.Name, state.Arguments.ToString(), options, "preparing"));
    }

    /// <summary>Record the id Pi settled on for a finished tool-call block, so the
    /// execution events can be matched back to the card the preview already put on
    /// screen even when the model sent the id last.</summary>
    private static void NoteFinishedToolCall(JsonElement ev, ToolPreviewTracker preview)
    {
        if (!ev.TryGetProperty("contentIndex", out var idx) || idx.ValueKind != JsonValueKind.Number)
            return;
        if (!ev.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object) return;

        preview.Finish(idx.GetInt32(), call);
    }

    private static string UnwrapToolResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    parts.Add(text.GetString() ?? "");
            }
            if (parts.Count > 0)
                return PrettyJson(string.Join("\n", parts));
        }

        // Unrecognised shape (a future Pi change, a non-text part): the raw value
        // beats showing nothing.
        return result.GetRawText();
    }

    /// <summary>Indent the tool's JSON output for the card, the way the direct
    /// provider does. Non-JSON output (plain stdout) is shown as-is.</summary>
    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, DisplayJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;

    /// <summary>Retiring a provider drops the sidecars shaped for it. The runtime
    /// itself outlives every provider — it is shared — so this deliberately does
    /// not tear it down.</summary>
    public ValueTask DisposeAsync() => new(_runtime.RetireSpecAsync(_config.Spec));
}

/// <summary>Static config for the provider: identity, models, and the shape of the
/// sidecar it needs. <see cref="ResolveCreds"/> is called per request so the
/// account-quota JWT (or a BYOK key) can be supplied live by the DI wiring.</summary>
public sealed record PiWorkProviderConfig(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<ProviderModel> Models,
    PiSidecarSpec Spec,
    Func<ChatRequest, PiProviderCreds> ResolveCreds);

/// <summary>Per-request billing target. Account-quota → the MolaGPT relay URL +
/// the live account JWT; BYOK → the user's own chat endpoint + key. Neither the
/// URL nor the token reaches the sidecar: both are consumed by
/// <see cref="PiWorkLlmShim"/> inside the desktop process.</summary>
/// <param name="Endpoint">Absolute URL of the real chat-completions endpoint.</param>
/// <param name="TokenProvider">Resolves the bearer token at request time, so a
/// rotating account JWT is always current without respawning the sidecar.</param>
/// <param name="DropBodyKeys">Request-body keys this endpoint rejects outright.
/// See <see cref="PiEndpointQuirks"/> for why the sidecar cannot work these out
/// for itself.</param>
public sealed record PiProviderCreds(
    string Endpoint,
    Func<CancellationToken, Task<string?>> TokenProvider,
    string Model,
    string Api = "openai-completions",
    bool Reasoning = false,
    Action? OnUnauthorized = null,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
    PiWorkLlmShim.AuthStyle Auth = PiWorkLlmShim.AuthStyle.Bearer,
    IReadOnlyList<string>? DropBodyKeys = null,
    PiWorkLlmShim.TargetPathMode PathMode = PiWorkLlmShim.TargetPathMode.Fixed);
