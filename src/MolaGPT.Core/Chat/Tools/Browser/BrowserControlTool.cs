using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// Agent-facing browser tool backed by the local Kimi WebBridge daemon.
/// One tool name, action-split capabilities so Approval mode auto-allows reads
/// and prompts only for writes (navigate / click / fill / close_session).
/// </summary>
public sealed partial class BrowserControlTool
{
    public const string ToolName = "browser";

    /// <summary>Grant keys are <c>browser:example.com</c>, never bare
    /// <c>browser</c> — see <see cref="PlanApprovalAsync"/>.</summary>
    public const string GrantPrefix = "browser:";

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly WebBridgeClient _client;
    private readonly Func<CancellationToken, Task<bool>>? _tryStartDaemon;

    public BrowserControlTool(WebBridgeClient client, Func<CancellationToken, Task<bool>>? tryStartDaemon = null)
    {
        _client = client;
        _tryStartDaemon = tryStartDaemon ?? WebBridgeClient.TryStartInstalledDaemonAsync;
    }

    public static object BuildOpenAiToolDefinition(BrowserControlOptions options) => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description =
                "Drive the user's real Chrome/Edge browser through the local Kimi WebBridge daemon "
                + "(login sessions stay on-device). Prefer this over web_fetch when the task needs "
                + "a logged-in site, multi-step UI interaction, or a screenshot of the live page. "
                + "A session owns its own tab group: start with navigate, which is what creates the tab — "
                + "every other page action fails until then. "
                + "Workflow: navigate → find (locate by keyword, returns @e refs) → click/fill → find or "
                + "snapshot to verify. Reach for a full snapshot only when you need to survey an unfamiliar "
                + "page; on a big one it costs tens of thousands of tokens, while find costs a few hundred. "
                + "read_page is the cheap way to read an article's text. "
                + "Use status to check whether the daemon and browser extension are connected. "
                + "Never enter passwords, payment details or one-time codes, and never solve captchas."
                + HostRuleHint(options),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "status", "list_tabs", "find", "snapshot", "read_page", "screenshot",
                            "scroll", "hover", "wait",
                            "navigate", "click", "fill", "select_option", "send_keys", "close_session"
                        },
                        description =
                            "Read-only: status, list_tabs, find, snapshot, read_page, screenshot, scroll, hover, wait. "
                            + "navigate/click/fill/select_option/send_keys/close_session change the browser "
                            + "and need approval in Approval mode."
                    },
                    url = new
                    {
                        type = "string",
                        description = "For navigate: absolute http(s) URL."
                    },
                    query = new
                    {
                        type = "string",
                        description = "For find: text to look for in the page's accessible names and labels. "
                            + "Returns every match with its role, its position in the page, and an @e ref you "
                            + "can click. This is the fast way to locate a control — reach for it before snapshot."
                    },
                    @ref = new
                    {
                        type = "string",
                        description = "For snapshot: an @e ref to expand instead of the whole page. Measured on "
                            + "one article page: whole page 57KB, one ref 345 bytes. Pair it with find."
                    },
                    selector = new
                    {
                        type = "string",
                        description = "An @e ref from find/snapshot, or a standard CSS selector. Nothing else "
                            + "parses — text=, XPath and :has-text() all fail. Used by click, fill, "
                            + "select_option, hover, screenshot, scroll (scroll the element into view) and "
                            + "wait (wait until it is visible)."
                    },
                    value = new
                    {
                        type = "string",
                        description = "For fill: text to place into the field (replaces existing content). "
                            + "For select_option: the option's value or its visible label; omit it to get the "
                            + "list of available options back instead of choosing one."
                    },
                    direction = new
                    {
                        type = "string",
                        @enum = new[] { "up", "down", "left", "right" },
                        description = "For scroll: one viewport in this direction (default down). The result "
                            + "carries scrollY/scrollHeight/atTop/atBottom, so you can tell whether more is left. "
                            + "Pass selector instead to scroll a specific element into view."
                    },
                    keys = new
                    {
                        type = "string",
                        description = "For send_keys: key names, not text — \"Enter\", \"Escape\", \"Mod+A\", "
                            + "\"Shift+Tab\", or several separated by spaces. Use fill to enter text."
                    },
                    text = new
                    {
                        type = "string",
                        description = "For wait: wait until this text appears on the page."
                    },
                    text_gone = new
                    {
                        type = "string",
                        description = "For wait: wait until this text disappears (a spinner, a 'saving…' notice)."
                    },
                    new_tab = new
                    {
                        type = "boolean",
                        description = "For navigate: open in a new tab. Use true on the first navigate of a task."
                    },
                    format = new
                    {
                        type = "string",
                        @enum = new[] { "png", "jpeg" },
                        description = "For screenshot: image format (default png)."
                    },
                    quality = new
                    {
                        type = "integer",
                        description = "For screenshot jpeg quality 0-100 (optional)."
                    }
                },
                required = new[] { "action" }
            }
        }
    };

    /// <summary>Tell the model about the user's site rules up front, so it plans
    /// around them instead of discovering them as refusals.</summary>
    private static string HostRuleHint(BrowserControlOptions options)
    {
        var allowed = options.AllowedHostList;
        var blocked = options.BlockedHostList;
        if (allowed.Count == 0 && blocked.Count == 0) return string.Empty;

        var parts = new List<string>();
        if (allowed.Count > 0)
            parts.Add($"Only these sites are permitted: {string.Join(", ", allowed)} (and their subdomains).");
        if (blocked.Count > 0)
            parts.Add($"These sites are blocked: {string.Join(", ", blocked)}.");
        return " " + string.Join(" ", parts);
    }

    /// <summary>
    /// Read vs write, which decides whether Approval mode prompts.
    ///
    /// scroll and hover sit on the read side deliberately. Neither submits
    /// anything or changes user data — they are what a mouse does on its way to
    /// somewhere — and putting a dialog in front of every scroll of a long page
    /// would train the user to click through the ones that matter.
    /// </summary>
    public static BrowserActionKind ClassifyAction(string? action) =>
        NormalizeAction(action) switch
        {
            "status" or "list_tabs" or "find" or "snapshot" or "read_page" or "screenshot"
                or "scroll" or "hover" or "wait" => BrowserActionKind.Read,
            "navigate" or "click" or "fill" or "select_option" or "send_keys" or "close_session"
                => BrowserActionKind.Write,
            _ => BrowserActionKind.Unknown
        };

    public static ToolCapability CapabilitiesFor(string? action) =>
        ClassifyAction(action) switch
        {
            // Fail closed: anything not explicitly a read is treated as a write
            // so Approval mode prompts instead of silently auto-approving.
            BrowserActionKind.Read => ToolCapability.Read | ToolCapability.External,
            _ => ToolCapability.Write | ToolCapability.External
        };

    public static string DisplayNameFor(string? action) =>
        NormalizeAction(action) switch
        {
            "status" => "浏览器状态",
            "list_tabs" => "浏览器标签",
            "find" => "查找页面元素",
            "snapshot" => "读取页面",
            "read_page" => "读取页面正文",
            "screenshot" => "浏览器截图",
            "scroll" => "滚动页面",
            "hover" => "悬停元素",
            "wait" => "等待页面",
            "navigate" => "打开网页",
            "click" => "浏览器点击",
            "fill" => "填写表单",
            "select_option" => "选择下拉项",
            "send_keys" => "发送按键",
            "close_session" => "关闭浏览器会话",
            _ => "浏览器"
        };

    public static string PendingLabelFor(string? action) =>
        NormalizeAction(action) switch
        {
            "status" => "正在检查浏览器",
            "list_tabs" => "正在列出浏览器标签",
            "find" => "正在查找页面元素",
            "snapshot" => "正在读取页面结构",
            "read_page" => "正在读取页面正文",
            "screenshot" => "正在截图",
            "scroll" => "正在滚动页面",
            "hover" => "正在悬停元素",
            "wait" => "正在等待页面",
            "navigate" => "正在打开网页",
            "click" => "正在点击页面元素",
            "fill" => "正在填写表单",
            "select_option" => "正在选择下拉项",
            "send_keys" => "正在发送按键",
            "close_session" => "正在关闭浏览器标签",
            _ => "正在操作浏览器"
        };

    // ---- approval planning -------------------------------------------------

    /// <summary>
    /// What the host needs to know before running this call: which site it lands
    /// on, whether the user's rules already settle it, and what a "始终允许"
    /// would actually be granting.
    ///
    /// The grant key always names a site (<c>browser:example.com</c>). A bare
    /// <c>browser</c> grant would mean "click and type anywhere, forever", which
    /// is not a thing anyone can meaningfully agree to in one dialog — so when
    /// the site cannot be determined, the call is marked ask-every-time instead
    /// and no grant can be recorded at all.
    /// </summary>
    public async Task<BrowserApprovalPlan> PlanApprovalAsync(
        string argumentsJson,
        BrowserControlOptions? options,
        string? conversationId,
        CancellationToken ct)
    {
        options ??= new BrowserControlOptions();
        var args = ParseArgs(argumentsJson);
        var action = NormalizeAction(args.Action);
        var capabilities = CapabilitiesFor(args.Action);
        var display = DisplayNameFor(args.Action);

        // 红线先于一切：卡号、证件号没有「问一次就可以填」的版本，所以它不进审批流。
        // 每一个会把字带进页面的参数都要过：漏掉一个，那条通道就是完全没有闸门的。
        if ((BrowserGuard.RefusalFor(action, args.Value)
             ?? BrowserGuard.RefusalFor(action, args.Keys)) is { } redLine)
        {
            return BrowserApprovalPlan.Refused(redLine);
        }

        var host = HostFromUrl(args.Url);

        // In-page actions carry no URL; ask the daemon which site the session is
        // sitting on so the dialog can name it and the grant can be scoped to it.
        if (host is null && NeedsSessionHost(action, options))
            host = await ResolveSessionHostAsync(options, conversationId, ct).ConfigureAwait(false);

        if (HostRules.Matches(options.BlockedHostList, host))
            return BrowserApprovalPlan.Refused($"{host} 在浏览器工具的禁止名单中，已拒绝。");

        if (options.AllowedHostList.Count > 0)
        {
            if (host is null)
            {
                // 站点解析不出来有两种原因，说错了会让人去改错东西：会话里根本没有
                // 标签页（浏览器没开、或者用户自己关了），和站点确实不在名单里。
                return BrowserApprovalPlan.Refused(
                    "无法确定当前页面所属网站，而浏览器工具设置了允许名单，已拒绝。"
                    + "如果本会话还没打开过页面，先 navigate 到允许名单内的网站；"
                    + "如果浏览器没开或扩展没连上，用 status 确认后交给用户处理。");
            }
            if (!HostRules.Matches(options.AllowedHostList, host))
                return BrowserApprovalPlan.Refused($"{host} 不在浏览器工具的允许名单中，已拒绝。");
        }

        // 受保护动作：站点级授权不覆盖它。「始终允许 example.com」授的是「在这个站点
        // 上操作」，不是「在这个站点上什么都行」——下载、授权页、结账页要单独再问。
        var protectedReason = BrowserGuard.ProtectedReason(action, args.Url, host, options);

        var detail = host is null ? display : $"{display} · {host}";
        if (protectedReason is not null) detail = $"{detail}（{protectedReason}）";

        // An allow-listed site is a standing authorization the user already made
        // in settings; re-asking per click would train them to click through.
        var preAuthorized = protectedReason is null
                            && host is not null
                            && HostRules.Matches(options.AllowedHostList, host);

        return new BrowserApprovalPlan(
            Action: action,
            GrantKey: host is null ? ToolName : GrantPrefix + host,
            DisplayName: detail,
            Capabilities: capabilities,
            Host: host,
            // 受保护动作不产生也不使用任何可记住的授权：这一次批了就是这一次。
            AlwaysAsk: host is null || protectedReason is not null,
            PreApproved: preAuthorized,
            ProtectedReason: protectedReason);
    }

    /// <summary>
    /// Whether it is worth a round trip to ask which site the session is on.
    /// Writes always need it — that is what the grant gets scoped to. Reads only
    /// need it when there are rules to enforce, so the common case (no rules)
    /// costs nothing.
    /// </summary>
    private static bool NeedsSessionHost(string? action, BrowserControlOptions options) => action switch
    {
        "click" or "fill" or "select_option" or "send_keys" => true,
        "snapshot" or "screenshot" or "find" or "read_page" or "scroll" or "hover" or "wait"
            => options.HasHostRules,
        _ => false
    };

    private async Task<string?> ResolveSessionHostAsync(
        BrowserControlOptions options,
        string? conversationId,
        CancellationToken ct)
    {
        try
        {
            var response = await _client.SendAsync(
                "list_tabs",
                new JsonObject(),
                BuildSessionId(conversationId),
                TimeSpan.FromSeconds(8),
                ct,
                options.DaemonUrl).ConfigureAwait(false);
            if (!response.Success) return null;

            using var doc = JsonDocument.Parse(response.BodyText);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data)) root = data;
            if (!root.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array) return null;

            foreach (var tab in tabs.EnumerateArray())
            {
                var host = HostFromUrl(tab.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null);
                if (host is not null) return host;
            }
        }
        catch (JsonException)
        {
            // Unknown host: the caller fails closed on its own terms.
        }
        return null;
    }

    private static string? HostFromUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
            ? uri.Host.ToLowerInvariant()
            : null;

    // ---- execution ---------------------------------------------------------

    public async Task<string> ExecuteAsync(
        string argumentsJson,
        BrowserControlOptions? options,
        string? conversationId,
        string? workspaceRoot,
        CancellationToken ct)
    {
        options ??= new BrowserControlOptions();
        if (!options.Enabled)
            return Error("浏览器工具未开启。请在设置 → 浏览器中开启「启用浏览器使用」，并确认本机已安装 Kimi 浏览器扩展。");

        var args = ParseArgs(argumentsJson);
        var action = NormalizeAction(args.Action);
        if (action is null)
            return Error("缺少或无法识别 action。支持：status, list_tabs, find, snapshot, read_page, screenshot, "
                         + "scroll, hover, wait, navigate, click, fill, select_option, send_keys, close_session。");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 180));

        // Daemon health is a different endpoint. Routing it through /command
        // dispatches it as a page tool, which fails with "session has no tab"
        // before the model ever learns whether the bridge is up.
        if (action == "status")
            return await FormatStatusAsync(options, timeout, ct).ConfigureAwait(false);

        var session = BuildSessionId(conversationId);

        JsonObject? daemonArgs;
        try
        {
            daemonArgs = BuildDaemonArgs(action, args, options, workspaceRoot, BuildGroupTitle(conversationId));
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }

        // navigate 是会话的开场，也是唯一一个在扩展没接上时会「假成功」的动作：实测
        // 空闲很久的 daemon 会返回 ok:true 和一个 tabId，而根本没有浏览器在跑，模型
        // 于是基于一个不存在的标签页往下编。先问一次 /status 就能把这条路堵死，代价
        // 是一次本机 HTTP。
        if (action == "navigate")
        {
            if (await CheckBridgeReadyAsync(options, ct).ConfigureAwait(false) is { } notReady)
                return Error(notReady);
        }

        var response = await SendWithOptionalWarmStartAsync(action, daemonArgs, session, timeout, options, ct)
            .ConfigureAwait(false);

        if (!response.Success)
            return Error(TranslateDaemonError(response.ErrorMessage));

        var body = PreferInnerData(response.BodyText);

        if (action == "screenshot")
            return FormatScreenshotResult(body);

        if (action is "snapshot" or "find" or "read_page")
            return FormatBulkReadResult(action, body, options.SnapshotMaxCharacters);

        return body;
    }

    /// <summary>
    /// 收掉某个对话留下的标签组，尽力而为。
    ///
    /// 不走审批：关掉我们自己开的标签不需要用户再同意一次，而留着才是问题——那是一组
    /// 没人认领、用户不知道该不该关的标签页。桥没起来就当已经关了，不去唤醒 daemon。
    /// </summary>
    public async Task CloseSessionAsync(
        string? conversationId,
        bool enabled,
        CancellationToken ct = default)
    {
        if (!enabled) return;
        try
        {
            await _client.SendAsync(
                "close_session",
                new JsonObject(),
                BuildSessionId(conversationId),
                TimeSpan.FromSeconds(6),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不值得打扰任何人。
        }
    }

    /// <summary>
    /// 桥能不能干活，不能的话该说什么。返回 null 表示就绪。
    /// </summary>
    private async Task<string?> CheckBridgeReadyAsync(BrowserControlOptions options, CancellationToken ct)
    {
        var status = await _client
            .GetStatusAsync(options.DaemonUrl, TimeSpan.FromSeconds(6), ct)
            .ConfigureAwait(false);

        if (!status.Running && _tryStartDaemon is not null && await _tryStartDaemon(ct).ConfigureAwait(false))
        {
            status = await _client
                .GetStatusAsync(options.DaemonUrl, TimeSpan.FromSeconds(8), ct)
                .ConfigureAwait(false);
        }

        if (!status.Running)
        {
            return WebBridgeAddress.IsInstalled
                ? $"本机服务未运行（{status.DaemonUrl}），已尝试启动但没有起来。"
                  + "请让用户在设置 → 浏览器中点「启动服务」，或在终端运行 kimi-webbridge start。"
                : "本机未安装 Kimi 浏览器扩展服务，浏览器工具不可用。"
                  + "请让用户在设置 → 浏览器 → 配置指引中完成配置。";
        }

        return status.ExtensionConnected ? null : TranslateDaemonError("no extension connected");
    }

    private async Task<string> FormatStatusAsync(
        BrowserControlOptions options,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var status = await _client.GetStatusAsync(options.DaemonUrl, timeout, ct).ConfigureAwait(false);

        if (!status.Running && _tryStartDaemon is not null && await _tryStartDaemon(ct).ConfigureAwait(false))
            status = await _client.GetStatusAsync(options.DaemonUrl, timeout, ct).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["success"] = status.Running && status.ExtensionConnected,
            ["daemon_running"] = status.Running,
            ["extension_connected"] = status.ExtensionConnected,
            ["daemon_url"] = status.DaemonUrl
        };
        if (status.DaemonVersion is not null) result["daemon_version"] = status.DaemonVersion;
        if (status.ExtensionVersion is not null) result["extension_version"] = status.ExtensionVersion;

        result["note"] = status switch
        {
            { Running: false } when WebBridgeAddress.IsInstalled =>
                "本机服务未运行。请让用户在设置 → 浏览器中启动，或在终端运行 kimi-webbridge start。",
            { Running: false } =>
                "本机未安装 Kimi 浏览器扩展服务，浏览器工具不可用。请让用户在设置 → 浏览器 → 配置指引中完成配置。",
            { ExtensionConnected: false } =>
                "服务已运行但浏览器扩展未连接。请让用户打开 Chrome/Edge 并确认扩展已启用。",
            _ => "服务与浏览器扩展均已就绪，可以 navigate。"
        };
        return result.ToJsonString(ResultJsonOptions);
    }

    /// <summary>Daemon payloads wrap the real body as <c>{ok, data}</c>; prefer the
    /// nested data so the agent sees the command result, not the transport
    /// envelope.</summary>
    private static string PreferInnerData(string bodyText)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return data.GetRawText();
            }
        }
        catch (JsonException)
        {
            // keep original
        }
        return bodyText;
    }

    private async Task<WebBridgeResponse> SendWithOptionalWarmStartAsync(
        string action,
        JsonObject? daemonArgs,
        string session,
        TimeSpan timeout,
        BrowserControlOptions options,
        CancellationToken ct)
    {
        var response = await _client
            .SendAsync(action, daemonArgs, session, timeout, ct, options.DaemonUrl)
            .ConfigureAwait(false);
        if (!response.ConnectionFailed || _tryStartDaemon is null)
            return response;

        // Binary already on disk but the listener is down — start once, then retry.
        var started = await _tryStartDaemon(ct).ConfigureAwait(false);
        if (!started)
            return response;

        return await _client
            .SendAsync(action, daemonArgs, session, timeout, ct, options.DaemonUrl)
            .ConfigureAwait(false);
    }

    private static JsonObject? BuildDaemonArgs(
        string action,
        BrowserToolArgs args,
        BrowserControlOptions options,
        string? workspaceRoot,
        string groupTitle)
    {
        switch (action)
        {
            case "list_tabs":
            case "close_session":
            case "read_page":
                return new JsonObject();
            case "snapshot":
            {
                // `ref` narrows; `selector` does not. Probed against the bridge:
                // a selector matching nothing at all still returns the whole page,
                // so forwarding it would only make the model think it worked.
                var obj = new JsonObject();
                if (!string.IsNullOrWhiteSpace(args.Ref))
                    obj["ref"] = args.Ref!.Trim();
                return obj;
            }
            case "find":
            {
                if (string.IsNullOrWhiteSpace(args.Query))
                    throw new ArgumentException("find 需要 query：要在页面上查找的文字。");
                return new JsonObject { ["query"] = args.Query!.Trim() };
            }
            case "scroll":
            {
                // selector wins when both are given: "scroll this into view" is a
                // definite request, while a direction is a guess about distance.
                if (!string.IsNullOrWhiteSpace(args.Selector))
                    return new JsonObject { ["selector"] = args.Selector!.Trim() };

                var direction = NormalizeAction(args.Direction) ?? "down";
                if (direction is not ("up" or "down" or "left" or "right"))
                    throw new ArgumentException("scroll 的 direction 只能是 up / down / left / right。");
                return new JsonObject { ["direction"] = direction };
            }
            case "hover":
            {
                if (string.IsNullOrWhiteSpace(args.Selector))
                    throw new ArgumentException("hover 需要 selector（@e 引用或 CSS 选择器）。");
                return new JsonObject { ["selector"] = args.Selector!.Trim() };
            }
            case "wait":
            {
                var obj = new JsonObject();
                if (!string.IsNullOrWhiteSpace(args.Text)) obj["text"] = args.Text!.Trim();
                if (!string.IsNullOrWhiteSpace(args.TextGone)) obj["text_gone"] = args.TextGone!.Trim();
                if (!string.IsNullOrWhiteSpace(args.Selector)) obj["selector"] = args.Selector!.Trim();
                if (obj.Count == 0)
                {
                    throw new ArgumentException(
                        "wait 需要一个条件：text（出现）、text_gone（消失）或 selector（可见）。"
                        + "它不是一个单纯的 sleep——要等的东西说清楚才等得准。");
                }
                return obj;
            }
            case "send_keys":
            {
                if (string.IsNullOrWhiteSpace(args.Keys))
                    throw new ArgumentException("send_keys 需要 keys，例如 \"Enter\"、\"Mod+A\"、\"Shift+Tab\"。输入文字请用 fill。");
                return new JsonObject { ["keys"] = args.Keys!.Trim() };
            }
            case "select_option":
            {
                if (string.IsNullOrWhiteSpace(args.Selector))
                    throw new ArgumentException("select_option 需要 selector，指向一个原生 <select>。");
                var obj = new JsonObject { ["selector"] = args.Selector!.Trim() };
                // 不给 value 时桥会把可选项列出来，这是有用的一步，不要当成缺参数。
                if (!string.IsNullOrWhiteSpace(args.Value))
                    obj["value"] = args.Value!.Trim();
                return obj;
            }
            case "navigate":
            {
                if (string.IsNullOrWhiteSpace(args.Url)
                    || !Uri.TryCreate(args.Url.Trim(), UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                {
                    // Same invariant as web_fetch: only public web pages. file://
                    // and chrome:// would let snapshot/screenshot read local disk
                    // or browser internals outside the file-tool approval path.
                    throw new ArgumentException("navigate 需要有效的 http/https 绝对 URL。");
                }
                if (HostRules.Matches(options.BlockedHostList, uri.Host))
                    throw new ArgumentException($"{uri.Host} 在浏览器工具的禁止名单中。");
                if (options.AllowedHostList.Count > 0 && !HostRules.Matches(options.AllowedHostList, uri.Host))
                    throw new ArgumentException($"{uri.Host} 不在浏览器工具的允许名单中。");

                return new JsonObject
                {
                    ["url"] = args.Url!.Trim(),
                    ["newTab"] = args.NewTab ?? true,
                    ["group_title"] = groupTitle
                };
            }
            case "click":
            {
                if (string.IsNullOrWhiteSpace(args.Selector))
                    throw new ArgumentException("click 需要 selector（优先使用 snapshot 的 @e 引用）。");
                return new JsonObject { ["selector"] = args.Selector!.Trim() };
            }
            case "fill":
            {
                if (string.IsNullOrWhiteSpace(args.Selector))
                    throw new ArgumentException("fill 需要 selector。");
                return new JsonObject
                {
                    ["selector"] = args.Selector!.Trim(),
                    ["value"] = args.Value ?? string.Empty
                };
            }
            case "screenshot":
            {
                var obj = new JsonObject();
                if (!string.IsNullOrWhiteSpace(args.Format))
                    obj["format"] = args.Format!.Trim() == "jpeg" ? "jpeg" : "png";
                if (args.Quality is { } q)
                    obj["quality"] = Math.Clamp(q, 0, 100);
                if (!string.IsNullOrWhiteSpace(args.Selector))
                    obj["selector"] = args.Selector!.Trim();

                // The daemon honours an explicit path; without one it writes to
                // its own temp folder, where the file is harder for the user to
                // find and for a later vision step to read.
                if (!string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    try
                    {
                        Directory.CreateDirectory(workspaceRoot!);
                        var ext = string.Equals(args.Format, "jpeg", StringComparison.OrdinalIgnoreCase)
                            ? "jpg"
                            : "png";
                        var path = Path.Combine(workspaceRoot!, $"browser-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.{ext}");
                        // Forward slashes: the daemon parses this straight out of
                        // JSON, where a Windows backslash is an escape character.
                        obj["path"] = path.Replace('\\', '/');
                    }
                    catch
                    {
                        // Fall back to the daemon's own temp path.
                    }
                }
                return obj;
            }
            default:
                throw new ArgumentException($"不支持的 action：{action}");
        }
    }

    /// <summary>
    /// Caps the three actions that can return a whole page, and — more usefully —
    /// says what to do about it.
    ///
    /// The note has been wrong twice, in opposite directions. It first said
    /// "可用 selector 缩小范围"; snapshot ignores selector, and one session followed
    /// that advice eleven times, paid for a full-page tree each time, and drove the
    /// turn past 3M prompt tokens. The correction then over-claimed that snapshot
    /// could not be narrowed at all — `ref` narrows it fine (57KB → 345 bytes on
    /// the page that was measured), and `find` is the right way to get a ref.
    /// Both readings came from probing one argument name and generalizing.
    /// </summary>
    private static string FormatBulkReadResult(string action, string bodyText, int maxCharacters)
    {
        maxCharacters = Math.Clamp(maxCharacters, 1000, 80000);
        if (bodyText.Length <= maxCharacters)
            return bodyText;

        // Truncating raw JSON hands the model an unparseable fragment. Wrap the
        // cut text as a string value instead, so what arrives is still JSON and
        // says plainly that it is incomplete.
        var cut = new JsonObject
        {
            ["truncated"] = true,
            ["note"] = action switch
            {
                "snapshot" => "页面结构过大，已截断。不要重试整页——用 find 按关键词定位，"
                              + "拿到 @e 引用后再 snapshot 带上 ref 只展开那一块。"
                              + "（selector 对 snapshot 无效，别用。）",
                "find" => "匹配太多，已截断。把 query 写得更具体一些，或换成页面上更独特的字样。",
                _ => "正文过长，已截断。用 find 定位你真正要读的那一段，再 snapshot 带 ref 展开。"
            },
            ["tree_text"] = bodyText[..maxCharacters]
        };
        return cut.ToJsonString(ResultJsonOptions);
    }

    /// <summary>
    /// 截图结果。
    ///
    /// 这里的 note 之前写的是「可用图片读取能力查看该路径」——等于在每张截图后面挂一句
    /// 「去分析我」。模型照做了：截图 → 图像分析 → 从像素里找元素，而这正是协议第一条
    /// 反对的事（定位用 snapshot 的 @e，不靠截图）。一次视觉调用换来的信息，snapshot
    /// 更准更便宜。所以 note 改成说清楚这张图是干什么用的，以及什么时候才值得看它。
    /// </summary>
    private static string FormatScreenshotResult(string bodyText)
    {
        // Surface a compact summary; keep the raw body for path/size details.
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            var path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            var summary = new JsonObject
            {
                ["success"] = true,
                ["path"] = path,
                ["note"] = string.IsNullOrWhiteSpace(path)
                    ? "截图已生成，但未返回本地路径。"
                    : "截图已保存，可在回答里用这个路径作为给用户的佐证。"
                      + "读页面文字或定位元素请用 snapshot，不要分析这张图；"
                      + "只有当问题本身是视觉的（排版、配色、图片内容）才值得再看一眼。"
            };
            if (root.TryGetProperty("sizeBytes", out var size) && size.ValueKind == JsonValueKind.Number)
                summary["sizeBytes"] = size.GetInt64();
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
                summary["format"] = format.GetString();
            if (root.TryGetProperty("mimeType", out var mime) && mime.ValueKind == JsonValueKind.String)
                summary["mimeType"] = mime.GetString();
            return summary.ToJsonString(ResultJsonOptions);
        }
        catch (JsonException)
        {
            return bodyText;
        }
    }

    public static string Error(string message) => JsonSerializer.Serialize(new
    {
        success = false,
        error = message
    }, ResultJsonOptions);

    /// <summary>
    /// Stable per-conversation tab-group key. Short enough for the daemon UI,
    /// unique enough that parallel conversations do not share tabs.
    /// </summary>
    public static string BuildSessionId(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return "mola-default";
        var slug = NonSlug().Replace(conversationId, "-").Trim('-');
        if (slug.Length == 0) return "mola-default";
        return slug.Length <= 8 ? $"mola-{slug}" : $"mola-{slug[^8..]}";
    }

    /// <summary>
    /// 标签组的名字，由我们生成而不是让模型编。
    ///
    /// 两个理由。一是这是我们在这套机制里**唯一免费拿到的产品界面**——agent 的工作
    /// 发生在用户自己的浏览器窗口里，标签组标签是他一眼能看到「这是 MolaGPT 在做的、
    /// 属于哪个对话」的地方；让模型每次自由发挥，它就既不稳定也不可认。
    ///
    /// 二是必须是 ASCII。实测这座桥会把标题过一遍 Windows ANSI 码页：发「莫拉验证」
    /// （UTF-8 <c>e8 8e ab …</c>），<c>list_tabs</c> 回显是 <c>Ī����֤</c>，正好是它的
    /// GBK 字节 <c>c4 aa c0 ad d1 e9 d6 a4</c> 被当成 UTF-8 读的结果。浏览器标签上显示
    /// 正常，但模型调 <c>list_tabs</c> 看到的是乱码——它会以为自己走错了标签组。
    /// ASCII 兼容 GBK，全程不变形。
    /// </summary>
    public static string BuildGroupTitle(string? conversationId)
    {
        var session = BuildSessionId(conversationId);
        var suffix = session.StartsWith("mola-", StringComparison.Ordinal) ? session[5..] : session;
        return $"MolaGPT {suffix}";
    }

    /// <summary>
    /// 把 daemon 的英文短句翻成模型能照着做下一步的话。
    ///
    /// 原样转发 "no extension connected" 的问题不是不礼貌，是它不含下一步：模型会去
    /// 重试、换选择器、或者干脆编一个结果。这两条是真实使用里最常见的失败，值得把
    /// 「现在该干什么」直接写进错误里。
    /// </summary>
    public static string TranslateDaemonError(string? message)
    {
        var text = message ?? "浏览器操作失败。";

        if (text.Contains("no extension connected", StringComparison.OrdinalIgnoreCase))
            return "浏览器扩展未连接：本机服务在运行，但 Chrome/Edge 没有接入。"
                   + "请让用户打开浏览器并确认 Kimi 浏览器扩展已启用，然后重试。"
                   + "如果浏览器明明开着仍然如此，最常见的原因是其他扩展冲突"
                   + "（爬虫类、网页助手、录屏、AI 助手类），可让用户临时只保留 Kimi 扩展再试。"
                   + "在用户确认之前不要反复重试。";

        if (text.Contains("has no tab", StringComparison.OrdinalIgnoreCase))
            return "本会话还没有标签页。先用 navigate 打开目标页面——"
                   + "如果刚才是用户自己关掉了标签组，那是他在喊停，先问清楚要不要继续，不要直接重开。";

        // 桥那边把 querySelector 抛出的 SyntaxError 原样吐成 "Uncaught"，而语法合法
        // 但找不到元素时给的是 "element not found: …"。两句话意思完全不同，模型却
        // 分不出来：实测它会以为是元素没找到，于是继续换写法——text=、XPath、
        // :has()——每一次都撞同一堵墙，一轮里白烧六次调用。
        if (text.Contains("Uncaught", StringComparison.OrdinalIgnoreCase))
            return "选择器语法不被支持。只接受两种：snapshot 返回的 @e 引用，或标准 CSS 选择器。"
                   + "text=、XPath（// 开头）、:has-text() 这类写法都会报这个错，换一种同类写法没有用——"
                   + "重新 snapshot 取 @e 引用。";

        return text;
    }

    private static BrowserToolArgs ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new BrowserToolArgs();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? n
                    : null;
            bool? Bool(string name) =>
                root.TryGetProperty(name, out var v)
                    ? v.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    }
                    : null;

            return new BrowserToolArgs(
                Str("action"),
                Str("url"),
                Str("selector"),
                Str("value"),
                Bool("new_tab") ?? Bool("newTab"),
                Str("group_title") ?? Str("groupTitle"),
                Str("format"),
                Int("quality"),
                Str("query"),
                Str("ref"),
                Str("direction"),
                Str("keys"),
                Str("text"),
                Str("text_gone") ?? Str("textGone"));
        }
        catch (JsonException)
        {
            return new BrowserToolArgs();
        }
    }

    private static string? NormalizeAction(string? action) =>
        string.IsNullOrWhiteSpace(action) ? null : action.Trim().ToLowerInvariant();

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonSlug();
}

public enum BrowserActionKind
{
    Unknown,
    Read,
    Write
}

/// <summary>
/// The approval shape of one browser call: which site, what to record if the
/// user says "always", and whether the user's settings already answered.
/// </summary>
public sealed record BrowserApprovalPlan(
    string? Action = null,
    string GrantKey = BrowserControlTool.ToolName,
    string DisplayName = "浏览器",
    ToolCapability Capabilities = ToolCapability.Write | ToolCapability.External,
    string? Host = null,
    bool AlwaysAsk = false,
    bool PreApproved = false,
    string? Refusal = null,
    string? ProtectedReason = null)
{
    public static BrowserApprovalPlan Refused(string reason) => new(Refusal: reason);

    /// <summary>站点授权对这一次无效，必须当场问。</summary>
    public bool IsProtected => ProtectedReason is not null;
}

internal sealed record BrowserToolArgs(
    string? Action = null,
    string? Url = null,
    string? Selector = null,
    string? Value = null,
    bool? NewTab = null,
    string? GroupTitle = null,
    string? Format = null,
    int? Quality = null,
    string? Query = null,
    string? Ref = null,
    string? Direction = null,
    string? Keys = null,
    string? Text = null,
    string? TextGone = null);
