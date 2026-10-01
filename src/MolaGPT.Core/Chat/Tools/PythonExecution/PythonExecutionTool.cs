using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

public sealed class PythonExecutionTool
{
    public const string ToolName = "execute_python_code";

    private const string UserScriptFileName = "main.py";
    internal const string RuntimeScriptPrefix = ".molagpt-run-";
    private const string RunnerScriptFileName = "runner.py";
    private static readonly TimeSpan ForegroundWaitBeforeBackground = TimeSpan.FromSeconds(13);

    /// <summary>Where a run's complete output goes when the inline copy had to be
    /// cut. Named so the model can find it, and excluded from artifact reporting so
    /// it does not look like something the user's code produced.</summary>
    private const string StdoutOverflowFileName = "stdout.full.log";
    private const string StderrOverflowFileName = "stderr.full.log";

    /// <summary>Where the audit hook records what a run touched. Read and deleted
    /// at the end of every run, and excluded from artifact reporting.</summary>
    private const string SandboxReportFileName = ".sandbox-report.jsonl";
    private const long MaxArtifactBytes = 50L * 1024L * 1024L;

    // Timestamp skew applied when deciding which files a run produced. Absorbs
    // filesystem mtime granularity (FAT/exFAT is 2s) plus minor clock jitter so
    // a freshly written artifact is never excluded as "too old".
    private static readonly TimeSpan ArtifactFreshnessSkew = TimeSpan.FromSeconds(2);

    /// <summary>Memory ceiling for one run's whole process tree. Set well above
    /// what real data work needs — this is here to stop a runaway allocation from
    /// taking the machine down, not to ration legitimate use.</summary>
    private const long JobMemoryLimitBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Process ceiling for one run's tree. Generous enough for code that
    /// legitimately shells out; low enough that a spawn loop stops early.</summary>
    private const int JobActiveProcessLimit = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IPythonExecutionApprovalService? _approval;
    private readonly IPythonSessionAllowList? _sessionAllowList;
    private readonly IToolGrantStore? _grants;
    private readonly AgentTaskRegistry? _tasks;
    private readonly IPythonAutoReviewer? _reviewer;

    public PythonExecutionTool(
        IPythonExecutionApprovalService? approval = null,
        IPythonSessionAllowList? sessionAllowList = null,
        IToolGrantStore? grants = null,
        AgentTaskRegistry? tasks = null,
        IPythonAutoReviewer? reviewer = null)
    {
        _approval = approval;
        _sessionAllowList = sessionAllowList;
        _grants = grants;
        _tasks = tasks;
        _reviewer = reviewer;
    }

    /// <param name="allowBackground">Offer <c>run_in_background</c>, describe the
    /// timeout as a move to the background rather than a kill, and say in the tool's
    /// own description that code may outlive the turn — a parameter description is
    /// read too late to change whether the model takes the job on.</param>
    public static object BuildOpenAiToolDefinition(PythonExecutionOptions options, bool allowBackground = false)
    {
        if (allowBackground)
            return WithBackground(BuildOpenAiToolDefinition(options));
        // In approval mode a confirmation dialog may appear before a run (any
        // code above the auto-approve bar), so the model must always state what
        // the code is for. Make `description` required there; optional otherwise.
        var requiresPurpose = options.PermissionMode == PythonPermissionMode.Approval;
        var descriptionHint = requiresPurpose
            ? "REQUIRED. One concise sentence, in the user's language, stating what this code does and why. It is shown to the user in an approval dialog before the code runs, so it must be specific (e.g. '用最小二乘法拟合这组散点并画出回归线'), not generic like 'run code'."
            : "Short user-facing purpose of this execution, in the user's language.";

        return new
        {
            type = "function",
            function = new
            {
                name = ToolName,
                // What this tool IS, and nothing else. How the workspace behaves —
                // shared directory, artifacts, pip, approvals, network — is stated
                // once in the system prompt's environment block instead of being
                // repeated in every tool schema.
                description = "Run Python code locally on the user's computer. This is a general-purpose local execution tool, similar to a bash/shell: prefer it whenever a task is better done by running code than by answering from memory — math and data analysis, reading and writing local files and folders, converting or generating documents, spreadsheets, images and plots, inspecting the system, and automating multi-step local tasks through the standard library (os, pathlib, shutil, subprocess). "
                    + "It runs real Python on the local machine with the user's own privileges and a persistent working directory, not a throwaway cloud sandbox. "
                    + "Print results and short progress to stdout so the user and you can see them. "
                    + (requiresPurpose
                        ? "Risky actions need the user's approval first, so always provide the `description` argument: they read it before deciding."
                        : $"Current execution permission mode is {options.PermissionMode}."),
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        code = new
                        {
                            type = "string",
                            description = "Python source code to run on the local machine. May be a complete multi-line script that imports modules, defines functions, touches the filesystem, and prints results. Write self-contained code that performs the whole step; print what matters to stdout."
                        },
                        description = new
                        {
                            type = "string",
                            description = descriptionHint
                        },
                        timeout_seconds = new
                        {
                            type = "integer",
                            description = "Seconds to allow this run before it is killed (optional). Raise it for work you expect to be slow; the host clamps it to a safe range."
                        },
                        paths = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "Folders outside the working directory this code needs to WRITE to, including the user's own folders such as the desktop (optional, but declare them whenever you already know them). Only the working directory is writable by default; declaring a folder turns what would otherwise be a failed run into one up-front question, and the user's answer is remembered. Reading is already allowed across the machine and never needs to be declared. An undeclared write fails with the refused path named — relay that to the user instead of retrying."
                        }
                    },
                    required = requiresPurpose ? new[] { "code", "description" } : new[] { "code" }
                }
            }
        };
    }

    private static object WithBackground(object definition)
    {
        var node = JsonSerializer.SerializeToNode(definition)!;
        var function = node["function"]!;
        function["description"] = (string)function["description"]!
            + " When background execution is available, use run_in_background for work likely to take longer than 13 seconds. A foreground run still active after 13 seconds then moves to the background and returns a task id.";
        var properties = function["parameters"]!["properties"]!.AsObject();
        properties["timeout_seconds"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "integer",
            ["description"] = "Timeout for foreground-only execution (optional). When background tasks are available, this does not extend the 13-second foreground execution window."
        };
        properties["run_in_background"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Return a task id at once and keep running in the background. Use it when the code may run longer than 13 seconds. You are notified when the process exits, so make it end when its work is done and print a summary last."
        };
        return node;
    }

    public Task<string> ExecuteAsync(
        string argumentsJson,
        PythonExecutionOptions? options,
        string? conversationId,
        CancellationToken ct) =>
        ExecuteAsync(argumentsJson, options, conversationId, null, ct);

    /// <param name="run">Who is calling and what the call may do beyond a plain
    /// foreground run. Null behaves as before: foreground only, approvals asked.</param>
    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PythonExecutionOptions? options,
        string? conversationId,
        PythonRunContext? run,
        CancellationToken ct)
    {
        if (options?.Enabled != true)
            return Error("Python tool is not enabled.");

        var (code, description, requestedTimeout, declaredPaths) = ParseArguments(argumentsJson);
        var wantsBackground = ReadBackgroundFlag(argumentsJson);
        if (string.IsNullOrWhiteSpace(code))
            return Error("A non-empty Python code string is required.");

        // Fold any session-scoped allow rules (granted earlier in this run via
        // the approval dialog) into the options BEFORE analysis, so the analyzer
        // sees the allowed modules/paths as known-safe and auto-approves them.
        var effectiveOptions = MergeSessionAllowList(options, _sessionAllowList, _grants);

        // The analyzer needs the working directory to tell "writes a chart next to
        // its own script" from "writes into the user's Documents folder".
        var workspaceRoot = ResolveSessionDirectory(conversationId);
        var risk = PythonExecutionRiskAnalyzer.Analyze(code!, effectiveOptions, workspaceRoot);

        // Which declared folders the run cannot already write to. Anything the
        // seed or an earlier grant already covers is not worth a question — the
        // model naming the desktop should not produce a prompt when the desktop
        // was already writable.
        var grantedPrefixes = SplitPrefixes(effectiveOptions.AllowedPathPrefixes);
        var seededWritable = PythonSandboxScope.DefaultWritableRoots(workspaceRoot, grantedPrefixes);
        var newScopeRequests = effectiveOptions.PermissionMode == PythonPermissionMode.FullAccess
            ? Array.Empty<string>()
            : declaredPaths
                .Select(ResolveDeclaredPath)
                .Where(p => p is not null)
                .Select(p => p!)
                .Where(p => !seededWritable.Any(root => WorkspaceScope.Covers(root, p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var permission = await ResolvePermissionAsync(
            code!, description, effectiveOptions, risk, newScopeRequests, workspaceRoot,
            run?.Unattended == true, run?.Review, ct).ConfigureAwait(false);
        if (!permission.Approved)
        {
            return Error(
                "本次执行未通过权限策略：" + permission.Reason,
                permission: BuildPermissionMeta(effectiveOptions, risk, "denied", permission.Review));
        }
        var permissionMeta = BuildPermissionMeta(effectiveOptions, risk, "approved", permission.Review);

        var maxOutput = Math.Clamp(options.MaxOutputCharacters, 2000, 100000);
        var sessionDir = workspaceRoot;
        var canBackground = run is { AllowBackground: true, Owner: not null, Unattended: false } && _tasks is not null;
        var timeout = canBackground
            ? ForegroundWaitBeforeBackground
            : TimeSpan.FromSeconds(Math.Clamp(requestedTimeout ?? options.TimeoutSeconds, 5, 300));
        var label = TaskLabel(description, code!);

        // Asked for the background, but there is no room: say so rather than
        // quietly running in the foreground for however long it takes.
        if (wantsBackground && canBackground
            && _tasks!.CheckCapacity(run!.Owner!.ConversationId, AgentTaskKinds.Python) is { } full)
            return Error(full + "等待已有任务结束，或改为前台运行。", sessionDir);

        var background = wantsBackground && canBackground;

        try
        {
            Directory.CreateDirectory(sessionDir);

            // Every run keeps its own scripts, logs and audit report. The user
            // script stays at the workspace root so __file__-relative outputs
            // continue to land with other conversation artifacts.
            var files = PythonRunFiles.ForRun(sessionDir);
            Directory.CreateDirectory(Path.GetDirectoryName(files.ScriptPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(files.ReportPath)!);
            await File.WriteAllTextAsync(files.ScriptPath, code!, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(files.RunnerPath, BuildRunnerScript(), new UTF8Encoding(false), ct).ConfigureAwait(false);
            var runnerScriptPath = files.RunnerPath;

            var python = await ResolvePythonAsync(options, ct).ConfigureAwait(false);

            // Capture the run-start instant AFTER writing the scripts but BEFORE
            // running, so ScanArtifacts can report only files this run created or
            // modified. The small skew absorbs filesystem timestamp granularity
            // and clock jitter. This is what keeps a reused (per-conversation)
            // working directory from re-reporting every earlier turn's images.
            var runStartUtc = DateTime.UtcNow - ArtifactFreshnessSkew;

            // Folders the user granted earlier extend the seeded scope, so a
            // "记住这个文件夹" decision keeps meaning the same thing here as it
            // does to the analyzer.
            // Approved declarations join the grants for this run. Re-reading the
            // grant store here is deliberate: the user may have ticked "记住"
            // in the dialog above, and that folder should take effect now rather
            // than on the next call.
            var scope = effectiveOptions.PermissionMode == PythonPermissionMode.FullAccess
                ? PythonSandboxScope.DenyOnly(options.AllowNetwork)
                : PythonSandboxScope.CreateDefault(
                    sessionDir,
                    python.FileName,
                    options.AllowNetwork,
                    grantedWritable: SplitPrefixes(effectiveOptions.AllowedPathPrefixes)
                        .Concat(_grants?.WritablePathPrefixes ?? Array.Empty<string>())
                        .Concat(newScopeRequests));

            AgentTaskReservation? reservation = null;
            if (background
                && !_tasks!.TryReserve(run!.Owner!, AgentTaskKinds.Python, out reservation, out var capacityError))
                return Error(capacityError! + "等待已有任务结束，或改为前台运行。", sessionDir);
            using var backgroundReservation = reservation;

            var process = StartPython(
                python,
                runnerScriptPath,
                sessionDir,
                maxOutput,
                options.AllowNetwork,
                scope,
                files);

            if (background)
            {
                AgentTask task;
                try
                {
                    task = HandOff(process, run!.Owner!, backgroundReservation!, label, sessionDir, runStartUtc, files);
                }
                catch
                {
                    process.Kill();
                    await process.WaitQuietlyAsync().ConfigureAwait(false);
                    process.Dispose();
                    throw;
                }
                return BackgroundStarted(task, description, sessionDir, files, permissionMeta, moved: false);
            }

            PythonRunResult outcome;
            var handedOff = false;
            try
            {
                var timedOut = false;
                try
                {
                    await process.Exited.WaitAsync(timeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    await process.WaitQuietlyAsync().ConfigureAwait(false);
                    throw;
                }

                // Out of time. Where the turn allows it, the run keeps going in the
                // background instead of being thrown away with everything it had done.
                if (timedOut && canBackground
                    && _tasks!.TryReserve(
                        run!.Owner!,
                        AgentTaskKinds.Python,
                        out var timeoutReservation,
                        out _))
                {
                    using (timeoutReservation!)
                    {
                        AgentTask task;
                        try
                        {
                            task = HandOff(process, run!.Owner!, timeoutReservation!, label, sessionDir, runStartUtc, files);
                        }
                        catch
                        {
                            process.Kill();
                            await process.WaitQuietlyAsync().ConfigureAwait(false);
                            throw;
                        }

                        handedOff = true;
                        return BackgroundStarted(task, description, sessionDir, files, permissionMeta, moved: true, timeout);
                    }
                }

                if (timedOut)
                {
                    process.Kill();
                    await process.WaitQuietlyAsync().ConfigureAwait(false);
                }
                outcome = process.Result(timedOut);
            }
            finally
            {
                if (!handedOff) process.Dispose();
            }

            var startedAt = process.Clock;
            var sandbox = ReadSandboxReport(files.ReportPath);
            var runOutcome = outcome;

            var scannedArtifacts = ScanArtifacts(sessionDir, runStartUtc);
            var artifacts = scannedArtifacts
                .Select(artifact => new
                {
                    name = artifact.Name,
                    relative_path = artifact.RelativePath,
                    path = artifact.Path,
                    content_type = artifact.ContentType,
                    bytes = artifact.Bytes,
                    truncated = artifact.Truncated
                })
                .ToArray();

            return JsonSerializer.Serialize(new
            {
                success = !runOutcome.TimedOut && runOutcome.ExitCode == 0,
                source = "local_python",
                description,
                python = python.DisplayName,
                working_directory = sessionDir,
                permission = permissionMeta,
                artifacts,
                display_instructions = BuildDisplayInstructions(scannedArtifacts),
                stdout = runOutcome.Stdout,
                stderr = runOutcome.Stderr,
                stdout_truncated = runOutcome.StdoutTruncated,
                stderr_truncated = runOutcome.StderrTruncated,
                // Present only when output was cut. Relative to this conversation's
                // working directory, so read_file / grep_files can open it directly.
                stdout_full_file = runOutcome.StdoutTruncated ? runOutcome.StdoutFullFile : null,
                stderr_full_file = runOutcome.StderrTruncated ? runOutcome.StderrFullFile : null,
                exit_code = runOutcome.ExitCode,
                duration_ms = (long)startedAt.Elapsed.TotalMilliseconds,
                timed_out = runOutcome.TimedOut,
                // What the run actually touched, and what it was refused. The
                // refusals matter most: they are the model's cue to explain what
                // it needs rather than retry the same call, and they are where a
                // payload that went looking for something becomes visible.
                sandbox_accessed = sandbox.Allowed,
                sandbox_denied = sandbox.Denied
            }, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message, sessionDir);
        }
    }

    /// <summary>
    /// Expands a declared path into an absolute one. <c>~</c> is resolved against
    /// the real profile — the same place the interpreter now resolves it — so a
    /// model writing "~/Desktop" asks for the folder the user would recognise.
    /// </summary>
    private static string? ResolveDeclaredPath(string declared)
    {
        var value = declared.Trim().Trim('"', '\'');
        if (value.Length == 0) return null;

        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return null;
            value = value.Length <= 1 ? profile : Path.Combine(profile, value[2..]);
        }

        var expanded = Environment.ExpandEnvironmentVariables(value);
        return Path.IsPathRooted(expanded) ? WorkspaceScope.Normalize(expanded) : null;
    }

    private async Task<PermissionDecision> ResolvePermissionAsync(
        string code,
        string? description,
        PythonExecutionOptions options,
        PythonExecutionRiskAnalysis risk,
        IReadOnlyList<string> newScopeRequests,
        string workspaceRoot,
        bool unattended,
        PythonReviewContext? review,
        CancellationToken ct)
    {
        // Layered permission filter (deny -> full-access -> auto-allow -> ask),
        // the model the industry converged on (Claude Code / Cursor / Windsurf).
        // The rules are no longer a separate mode; they are a filter that always
        // sits on top of the approval flow. Where a call would be asked about,
        // automatic review, when on, answers first.

        // [1] Deny layer: hard-denied code is rejected even under full access.
        if (risk.HardDenied)
            return new PermissionDecision(false, risk.BlockReason ?? "已被拒绝规则拦截");

        // Package installation always needs a separate, explicit decision. It
        // persists into this conversation's .packages directory and therefore
        // must not be hidden by FullAccess or a remembered import/path rule.
        if (risk.Flags.Any(flag => string.Equals(flag.Code, "package_install", StringComparison.Ordinal)))
        {
            return await AskAsync(
                code, description, options, risk, newScopeRequests, workspaceRoot, unattended, review,
                "用户已批准包安装", "用户拒绝了包安装", "包安装需要审批，但审批服务不可用", ct).ConfigureAwait(false);
        }

        // Destructive filesystem operations stay reviewable even when the user
        // selected FullAccess, matching the global tool policy.
        if (risk.Flags.Any(flag => string.Equals(flag.Code, "destructive_file", StringComparison.Ordinal)))
        {
            return await AskAsync(
                code, description, options, risk, newScopeRequests, workspaceRoot, unattended, review,
                "用户已批准该文件操作", "用户拒绝了该文件操作", "该文件操作需要审批，但审批服务不可用", ct).ConfigureAwait(false);
        }

        // [2] Full access: trust everything that survived the deny layer.
        if (options.PermissionMode == PythonPermissionMode.FullAccess)
            return new PermissionDecision(true, "完全权限模式已放行");

        // [3] Allow layer: nothing risky found and nothing new to grant ->
        // auto-approve without a prompt. A declared folder the run could already
        // write to is not "something new", so naming the desktop stays silent.
        if (risk.AutoApprovable && newScopeRequests.Count == 0)
            return new PermissionDecision(true, "未发现需要审批的操作，已自动放行");

        // [4] Everything else needs an explicit decision.
        return await AskAsync(
            code, description, options, risk, newScopeRequests, workspaceRoot, unattended, review,
            "用户已批准本次执行", "用户拒绝了本次执行", "需要审批，但审批服务不可用", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One decision: the reviewer if it is on, then the user for whatever it did
    /// not approve. A sub-agent has no user to fall back on, so there the
    /// reviewer's refusal is final — and is passed on, so the agent can say why
    /// rather than only that it could not.
    /// </summary>
    private async Task<PermissionDecision> AskAsync(
        string code,
        string? description,
        PythonExecutionOptions options,
        PythonExecutionRiskAnalysis risk,
        IReadOnlyList<string> newScopeRequests,
        string workspaceRoot,
        bool unattended,
        PythonReviewContext? review,
        string approvedReason,
        string deniedReason,
        string unavailableReason,
        CancellationToken ct)
    {
        var verdict = review is null || _reviewer is null
            ? null
            : await _reviewer.ReviewAsync(
                new PythonReviewRequest(code, description, risk, newScopeRequests, workspaceRoot, options.AllowNetwork, review),
                ct).ConfigureAwait(false);
        if (verdict is { Approved: true })
            return new PermissionDecision(true, "自动审批已放行", verdict);

        if (unattended)
            return new PermissionDecision(false, verdict is null ? UnattendedReason : verdict.Describe() + "。" + UnattendedReason, verdict);
        if (_approval is null)
            return new PermissionDecision(false, unavailableReason, verdict);

        var decision = await _approval.RequestApprovalAsync(
            new PythonExecutionApprovalRequest(
                code, description, options, risk, BuildCapabilities(options, risk), newScopeRequests, verdict?.Describe()),
            ct).ConfigureAwait(false);
        return decision == PythonExecutionApprovalDecision.Approved
            ? new PermissionDecision(true, approvedReason, verdict)
            : new PermissionDecision(false, deniedReason, verdict);
    }

    /// <summary>
    /// Folds the rules granted outside the settings page into the options the
    /// analyzer sees: imports allowed for this session, and folders the user chose
    /// to remember from the approval dialog.
    ///
    /// Doing it here rather than teaching the analyzer about grants keeps one
    /// notion of "allowed prefix" — the analyzer already treats those as
    /// unremarkable, so a remembered folder simply stops producing prompts.
    /// </summary>
    private static PythonExecutionOptions MergeSessionAllowList(
        PythonExecutionOptions options,
        IPythonSessionAllowList? sessionAllowList,
        IToolGrantStore? grants)
    {
        var imports = sessionAllowList?.Imports ?? Array.Empty<string>();

        // Only read-write grants count. A folder the user let read_file look at is
        // not a folder they let Python write to.
        var granted = grants?.WritablePathPrefixes ?? Array.Empty<string>();

        if (imports.Count == 0 && granted.Count == 0)
            return options;

        return options with
        {
            AllowedImports = AppendList(options.AllowedImports, imports),
            AllowedPathPrefixes = AppendList(options.AllowedPathPrefixes, granted)
        };
    }

    private static string AppendList(string? existing, IReadOnlyCollection<string> additions)
    {
        if (additions.Count == 0)
            return existing ?? string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing))
            parts.Add(existing!.Trim());
        parts.AddRange(additions);
        return string.Join(",", parts);
    }

    private static async Task<PythonCandidate> ResolvePythonAsync(PythonExecutionOptions options, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var candidate in BuildPythonCandidates(options.ExecutablePath))
        {
            try
            {
                var version = await ProbePythonAsync(candidate, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(version))
                    return candidate with { DisplayName = $"{candidate.DisplayName} ({version.Trim()})" };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{candidate.DisplayName}: {ex.Message}");
            }
        }

        var detail = failures.Count == 0 ? string.Empty : " " + string.Join(" | ", failures.Take(3));
        throw new InvalidOperationException("未找到可用的 Python 环境，请在设置中完成配置" + detail);
    }

    private static IEnumerable<PythonCandidate> BuildPythonCandidates(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            yield break;

        var trimmed = configuredPath.Trim().Trim('"');
        if (File.Exists(trimmed))
            yield return new PythonCandidate(Path.GetFullPath(trimmed), Array.Empty<string>(), Path.GetFullPath(trimmed));
    }

    private static async Task<string?> ProbePythonAsync(PythonCandidate candidate, CancellationToken ct)
    {
        using var process = CreateProcess(
            candidate,
            Array.Empty<string>(),
            Environment.CurrentDirectory,
            allowNetwork: false,
            configureSessionEnvironment: false);
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? (stdout + stderr).Trim() : null;
    }

    /// <summary>
    /// Start one run and return it still running. The caller decides whether to
    /// wait for it, give up on it, or hand it to the task registry — which is why
    /// the process, its output and its job object live on one disposable object
    /// instead of in the scope of a single await.
    /// </summary>
    private static PythonProcessRun StartPython(
        PythonCandidate candidate,
        string runnerScriptPath,
        string workingDirectory,
        int maxOutputCharacters,
        bool allowNetwork,
        PythonSandboxScope? scope,
        PythonRunFiles files)
    {
        var process = CreateProcess(candidate, new[] { "-I", "-X", "utf8", "-u", runnerScriptPath }, workingDirectory, allowNetwork);
        if (scope is not null)
            process.StartInfo.Environment["MOLAGPT_SANDBOX_SCOPE"] = scope.ToJson();
        process.StartInfo.Environment["MOLAGPT_MAIN_SCRIPT"] = files.ScriptPath;
        process.StartInfo.Environment["MOLAGPT_SANDBOX_REPORT"] = files.ReportPath;

        var stdout = new BoundedTextCollector(maxOutputCharacters, files.StdoutPath, files.Mirror);
        var stderr = new BoundedTextCollector(maxOutputCharacters, files.StderrPath, files.Mirror);
        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        // Caps the whole process tree and guarantees its teardown. Disposed at the
        // end of the run, which is what kills anything still alive.
        var job = OperatingSystem.IsWindows()
            ? WindowsJobObject.TryCreate(JobMemoryLimitBytes, JobActiveProcessLimit)
            : null;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Python process failed to start.");
        }
        catch
        {
            job?.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            process.Dispose();
            throw;
        }

        // Assigned immediately after start. A child spawned in the microseconds
        // before this lands would escape the job; closing that window needs
        // CREATE_SUSPENDED, which Process.Start cannot do. Acceptable here because
        // the job is a resource guard, not a security boundary.
        job?.TryAssign(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new PythonProcessRun(process, job, stdout, stderr, files);
    }

    private sealed class PythonProcessRun : IDisposable
    {
        private readonly Process _process;
        private readonly WindowsJobObject? _job;
        private readonly BoundedTextCollector _stdout;
        private readonly BoundedTextCollector _stderr;
        private readonly PythonRunFiles _files;

        public PythonProcessRun(
            Process process,
            WindowsJobObject? job,
            BoundedTextCollector stdout,
            BoundedTextCollector stderr,
            PythonRunFiles files)
        {
            _process = process;
            _job = job;
            _stdout = stdout;
            _stderr = stderr;
            _files = files;
            Clock = Stopwatch.StartNew();
            // Awaits the output streams too, so every line is in before it completes.
            Exited = process.WaitForExitAsync(CancellationToken.None);
        }

        public Stopwatch Clock { get; }
        public Task Exited { get; }

        public void Kill() => TryKill(_process);

        public async Task WaitQuietlyAsync()
        {
            try { await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { /* the job object ends it on dispose */ }
        }

        public string StdoutTail() => _stdout.Tail();
        public string StderrTail() => _stderr.Tail();

        public string Tail()
        {
            var err = _stderr.Tail();
            return string.IsNullOrWhiteSpace(err) ? _stdout.Tail() : _stdout.Tail() + "\n[stderr]\n" + err;
        }

        public PythonRunResult Result(bool timedOut)
        {
            _process.WaitForExit();
            Clock.Stop();
            return new PythonRunResult(
                _process.ExitCode,
                _stdout.Text,
                _stderr.Text,
                _stdout.Truncated,
                _stderr.Truncated,
                timedOut,
                _stdout.OverflowPath is null ? null : _files.StdoutRelative,
                _stderr.OverflowPath is null ? null : _files.StderrRelative);
        }

        public void Dispose()
        {
            _stdout.Dispose();
            _stderr.Dispose();
            _job?.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>
    /// Where one run keeps its scripts, output logs and audit report. The user
    /// script lives at the conversation root for __file__-relative paths; the
    /// runner, logs and report live under the run's private directory.
    /// </summary>
    private sealed record PythonRunFiles(
        string ScriptPath,
        string RunnerPath,
        string ReportPath,
        string StdoutPath,
        string StderrPath,
        string StdoutRelative,
        string StderrRelative,
        bool Mirror)
    {
        public const string TasksDirectoryName = ".tasks";

        public static PythonRunFiles ForRun(string sessionDir)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var name = "run-" + id;
            var dir = Path.Combine(sessionDir, TasksDirectoryName, name);
            var relative = TasksDirectoryName + "/" + name + "/";
            return new PythonRunFiles(
                Path.Combine(sessionDir, RuntimeScriptPrefix + id + ".py"),
                Path.Combine(dir, RunnerScriptFileName),
                Path.Combine(dir, "sandbox-report.jsonl"),
                Path.Combine(dir, "stdout.log"),
                Path.Combine(dir, "stderr.log"),
                relative + "stdout.log",
                relative + "stderr.log",
                Mirror: true);
        }
    }

    /// <summary>A background run's longest life. Long enough for real batch work,
    /// short enough that a forgotten loop does not run until the machine sleeps.</summary>
    private static readonly TimeSpan BackgroundCeiling = TimeSpan.FromHours(1);

    /// <summary>
    /// Give a started run to the task registry. From here it answers to the task's
    /// own cancellation, not to the turn's: the turn is about to end, and ending it
    /// must not end the run.
    /// </summary>
    private AgentTask HandOff(
        PythonProcessRun process,
        AgentTaskOwner owner,
        AgentTaskReservation reservation,
        string label,
        string sessionDir,
        DateTime runStartUtc,
        PythonRunFiles files)
    {
        return _tasks!.Start(reservation, label, async (_, taskCt) =>
        {
            using (process)
            {
                var timedOut = false;
                try
                {
                    var left = BackgroundCeiling - process.Clock.Elapsed;
                    await process.Exited.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, taskCt).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    process.Kill();
                    await process.WaitQuietlyAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    await process.WaitQuietlyAsync().ConfigureAwait(false);
                    throw;
                }

                var result = process.Result(timedOut);
                var sandbox = ReadSandboxReport(files.ReportPath);
                var artifacts = ScanArtifacts(sessionDir, runStartUtc);
                var report = JsonSerializer.Serialize(new
                {
                    success = !timedOut && result.ExitCode == 0,
                    exit_code = result.ExitCode,
                    duration_ms = (long)process.Clock.Elapsed.TotalMilliseconds,
                    timed_out = timedOut,
                    stdout_tail = process.StdoutTail(),
                    stderr_tail = process.StderrTail(),
                    // Relative to the working directory, for read_file.
                    stdout_file = result.StdoutFullFile,
                    stderr_file = result.StderrFullFile,
                    artifacts = artifacts.Select(a => new { name = a.Name, relative_path = a.RelativePath, bytes = a.Bytes }).ToArray(),
                    display_instructions = BuildDisplayInstructions(artifacts),
                    sandbox_denied = sandbox.Denied
                }, ReportJsonOptions);
                return new AgentTaskOutcome(!timedOut && result.ExitCode == 0, report);
            }
        }, process.Tail, process.Clock.Elapsed);
    }

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string BackgroundStarted(
        AgentTask task,
        string? description,
        string sessionDir,
        PythonRunFiles files,
        object permission,
        bool moved,
        TimeSpan timeout = default)
    {
        return JsonSerializer.Serialize(new
        {
            success = true,
            source = "local_python",
            background = true,
            task_id = task.Id,
            status = "running",
            description,
            working_directory = sessionDir,
            permission,
            stdout_file = files.Mirror ? files.StdoutRelative : null,
            stderr_file = files.Mirror ? files.StderrRelative : null,
            partial_stdout = moved ? task.Tail() : null,
            note = moved
                ? $"运行超过 {(int)timeout.TotalSeconds} 秒，已转入后台继续运行。"
                  + "结束时会自动通知你；先推进独立工作。已有足够信息时先回答并结束本轮，完成后再补充；确需当前结果时才用 task_status 按 ID 限时等待。到期仍在运行就告知进展并结束本轮，不要连续等待或重复启动。"
                : "已在后台运行。结束时会自动通知你；先推进独立工作。已有足够信息时先回答并结束本轮，完成后再补充；确需当前结果时才用 task_status 按 ID 限时等待。到期仍在运行就告知进展并结束本轮，不要连续等待或重复启动。"
        }, ReportJsonOptions);
    }

    private static string TaskLabel(string? description, string code)
    {
        var text = !string.IsNullOrWhiteSpace(description)
            ? description!
            : code.Replace("\r", "").Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "Python";
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= 40 ? text : text[..40] + "…";
    }

    private static bool ReadBackgroundFlag(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("run_in_background", out var flag)
                   && (flag.ValueKind == JsonValueKind.True
                       || (flag.ValueKind == JsonValueKind.String && flag.GetString() == "true"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const string UnattendedReason = "子 Agent 无法请求用户授权，这一步需要交给主 Agent。";

    private static Process CreateProcess(
        PythonCandidate candidate,
        IReadOnlyList<string> extraArgs,
        string workingDirectory,
        bool allowNetwork,
        bool configureSessionEnvironment = true)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName = candidate.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in candidate.PrefixArguments)
            process.StartInfo.ArgumentList.Add(arg);
        foreach (var arg in extraArgs)
            process.StartInfo.ArgumentList.Add(arg);

        // Start from a small, deterministic environment. In particular, do not
        // inherit PATH/PYTHONPATH/VIRTUAL_ENV/pip configuration from the desktop
        // process: those are the routes by which a bare `python` or `pip` could
        // accidentally mutate another interpreter.
        process.StartInfo.Environment.Clear();
        CopyEnvironmentIfPresent(process.StartInfo, "SystemRoot");
        CopyEnvironmentIfPresent(process.StartInfo, "WINDIR");
        CopyEnvironmentIfPresent(process.StartInfo, "COMSPEC");
        CopyEnvironmentIfPresent(process.StartInfo, "NUMBER_OF_PROCESSORS");
        CopyEnvironmentIfPresent(process.StartInfo, "PROCESSOR_ARCHITECTURE");

        var pythonDirectory = Path.GetDirectoryName(Path.GetFullPath(candidate.FileName))!;
        var scriptsDirectory = Path.Combine(pythonDirectory, "Scripts");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // Windows PowerShell lives beside system32 rather than in it, so a PATH of
        // just system32 left a bare `powershell` raising FileNotFoundError — the
        // one CLI a model on Windows reaches for first. Nothing is gained by
        // making it hunt for the full path: it can shell out either way.
        var powerShellDirectory = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");
        process.StartInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[] { pythonDirectory, scriptsDirectory, systemDirectory, powerShellDirectory }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        if (configureSessionEnvironment)
        {
            var tempDirectory = Path.Combine(workingDirectory, ".tmp");
            var packageDirectory = Path.Combine(workingDirectory, ".packages");
            var pipCacheDirectory = Path.Combine(workingDirectory, ".pip-cache");
            var appDataDirectory = Path.Combine(workingDirectory, ".appdata");
            var localAppDataDirectory = Path.Combine(workingDirectory, ".localappdata");
            Directory.CreateDirectory(tempDirectory);
            Directory.CreateDirectory(appDataDirectory);
            Directory.CreateDirectory(localAppDataDirectory);
            process.StartInfo.Environment["TEMP"] = tempDirectory;
            process.StartInfo.Environment["TMP"] = tempDirectory;

            // The user's OWN folders stay real. Pointing HOME/USERPROFILE at the
            // sandbox bought no security — a child process can set whatever
            // environment it likes, and absolute paths reached the real disk
            // regardless — while breaking every intuition a model has: `~`,
            // Path.home() and expanduser("~/Desktop") all silently resolved into
            // an empty sandbox folder. "Save it to my desktop" therefore wrote a
            // file nobody could find, and no approval dialog appeared, because a
            // path assembled at runtime is invisible to the analyzer. Real values
            // make the honest failure (a prompt, or an error) replace the silent
            // one, and cost nothing that was ever actually being protected.
            CopyEnvironmentIfPresent(process.StartInfo, "USERPROFILE");
            CopyEnvironmentIfPresent(process.StartInfo, "HOMEDRIVE");
            CopyEnvironmentIfPresent(process.StartInfo, "HOMEPATH");
            CopyEnvironmentIfPresent(process.StartInfo, "USERNAME");
            var realHome = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(realHome))
                process.StartInfo.Environment["HOME"] = realHome;

            // Program configuration directories stay redirected. No ordinary task
            // reads or writes them, so containing library config churn here costs
            // the model nothing — this is the half of the redirect that pays.
            process.StartInfo.Environment["APPDATA"] = appDataDirectory;
            process.StartInfo.Environment["LOCALAPPDATA"] = localAppDataDirectory;
            process.StartInfo.Environment["PIP_TARGET"] = packageDirectory;
            process.StartInfo.Environment["PIP_CACHE_DIR"] = pipCacheDirectory;

            // `pip install --target` drops console scripts under the target rather
            // than anywhere on PATH, so a freshly installed tool could be imported
            // but not run: `pyinstaller ...` and shutil.which("pyinstaller") both
            // failed while pyinstaller.exe sat in .packages/bin. Put those on PATH.
            // Added unconditionally — the directories appear only after the first
            // install, and a PATH entry that does not exist yet costs nothing.
            process.StartInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                new[]
                {
                    Path.Combine(packageDirectory, "bin"),      // pip's layout under --target
                    Path.Combine(packageDirectory, "Scripts"),  // what some versions use on Windows
                    process.StartInfo.Environment["PATH"],
                }.Where(p => !string.IsNullOrEmpty(p)));
            process.StartInfo.Environment["UV_CACHE_DIR"] = Path.Combine(workingDirectory, ".uv-cache");
        }
        else
        {
            CopyEnvironmentIfPresent(process.StartInfo, "TEMP");
            CopyEnvironmentIfPresent(process.StartInfo, "TMP");
        }
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.StartInfo.Environment["PYTHONUTF8"] = "1";
        process.StartInfo.Environment["PYTHONNOUSERSITE"] = "1";
        process.StartInfo.Environment["PYTHONSAFEPATH"] = "1";
        process.StartInfo.Environment["MPLBACKEND"] = "Agg";
        process.StartInfo.Environment["MPLCONFIGDIR"] = Path.Combine(workingDirectory, ".matplotlib");
        process.StartInfo.Environment["MOLAGPT_PYTHON_ALLOW_NETWORK"] = allowNetwork ? "1" : "0";
        process.StartInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        process.StartInfo.Environment["PIP_CONFIG_FILE"] = "NUL";
        if (!allowNetwork)
            process.StartInfo.Environment["PIP_NO_INDEX"] = "1";
        return process;
    }

    private static ToolCapability BuildCapabilities(
        PythonExecutionOptions options,
        PythonExecutionRiskAnalysis risk)
    {
        var capabilities = ToolCapability.Read | ToolCapability.Write;
        if (options.AllowNetwork
            || risk.Flags.Any(flag => flag.Code is "network_import" or "network_call" or "package_install"))
        {
            capabilities |= ToolCapability.External;
        }
        if (risk.Flags.Any(flag => flag.Code == "destructive_file"))
            capabilities |= ToolCapability.Destructive;
        return capabilities;
    }

    private static void CopyEnvironmentIfPresent(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            startInfo.Environment[name] = value;
    }

    private static string BuildRunnerScript() =>
        """
        import json
        import os
        import runpy
        import site
        import sys

        os.environ.setdefault("PYTHONIOENCODING", "utf-8")
        os.environ.setdefault("PYTHONUTF8", "1")
        os.environ.setdefault("MPLBACKEND", "Agg")

        # ---- sandbox scope -------------------------------------------------
        # Enforced here, inside the interpreter, because this is the only place
        # the real path is known. Static analysis of the source sees the literal
        # "Desktop" in os.path.join(os.environ["USERPROFILE"], "Desktop", name)
        # and cannot tell it is a path at all; the audit hook is handed the
        # finished string at the moment the file is opened.
        #
        # Not a security boundary: code in this process could reach around the
        # hook with ctypes. It stops mistakes and ordinary injected payloads,
        # which is what it is for.
        _scope = json.loads(os.environ.get("MOLAGPT_SANDBOX_SCOPE") or "{}")
        _readable = tuple(_scope.get("readable") or ())
        _writable = tuple(_scope.get("writable") or ())
        _denied = tuple(_scope.get("denied") or ())
        _allow_network = bool(_scope.get("allow_network"))
        # Two independent switches. Full-access runs carry a deny list and no
        # path scope: the user turned prompting off, so a scope violation would
        # have no way to be granted and would strand the task — but "never this
        # file" still holds, because that one is not a prompt in any mode.
        _enforce_scope = bool(_readable or _writable)
        _enforce = _enforce_scope or bool(_denied)

        # Report file opened BEFORE the hook exists, and written afterwards with
        # os.write on the raw descriptor. Using open() from inside the hook would
        # fire the hook again.
        _report_fd = -1
        if _enforce:
            try:
                _report_fd = os.open(
                    os.environ.get("MOLAGPT_SANDBOX_REPORT") or os.path.join(os.getcwd(), ".sandbox-report.jsonl"),
                    os.O_WRONLY | os.O_CREAT | os.O_TRUNC)
            except OSError:
                _report_fd = -1

        _seen = set()

        def _norm(value):
            try:
                return os.path.normcase(os.path.abspath(os.fspath(value)))
            except Exception:
                return None

        def _under(path, roots):
            for root in roots:
                r = os.path.normcase(root)
                if path == r or path.startswith(r.rstrip("\\/") + os.sep):
                    return True
            return False

        def _record(kind, target, allowed):
            key = (kind, target, allowed)
            if key in _seen:
                return
            _seen.add(key)
            if _report_fd < 0:
                return
            try:
                line = json.dumps(
                    {"kind": kind, "target": target, "allowed": allowed},
                    ensure_ascii=False) + "\n"
                os.write(_report_fd, line.encode("utf-8", "replace"))
            except Exception:
                pass

        # open() reports (path, mode, flags). A missing mode means a read path
        # inside CPython internals; treat anything without a write intent as a
        # read so the narrower list is never applied by accident.
        _WRITE_CHARS = ("w", "a", "x", "+")

        def _check_path(target, writing, label):
            path = _norm(target)
            if path is None:
                return
            if _under(path, _denied):
                _record(label, path, False)
                raise PermissionError(
                    "MolaGPT 沙盒：该文件受保护，不可访问\n  " + str(target))
            if not _enforce_scope:
                return
            roots = _writable if writing else _readable
            if _under(path, roots):
                _record(label, path, True)
                return
            _record(label, path, False)
            raise PermissionError(
                "MolaGPT 沙盒：路径超出本次批准范围\n"
                "  尝试" + ("写入" if writing else "读取") + "：" + str(target) + "\n"
                "  已批准" + ("写入" if writing else "读取") + "：\n    "
                + "\n    ".join(roots or ("（无）",))
                + "\n请说明需要访问该位置的原因，由用户授权后重试。")

        def _hook(event, args):
            if not _enforce:
                return
            if event == "open":
                target, mode, _flags = args
                writing = bool(mode) and any(c in mode for c in _WRITE_CHARS)
                _check_path(target, writing, "write" if writing else "read")
            elif event in ("os.remove", "os.rmdir", "os.unlink", "os.truncate"):
                _check_path(args[0], True, "delete")
            elif event == "os.rename" or event == "os.replace":
                _check_path(args[0], True, "delete")
                _check_path(args[1], True, "write")
            elif event == "os.mkdir":
                _check_path(args[0], True, "write")
            elif event in ("shutil.copyfile", "shutil.copymode", "shutil.copystat"):
                _check_path(args[0], False, "read")
                _check_path(args[1], True, "write")
            elif event == "shutil.move":
                _check_path(args[0], True, "delete")
                _check_path(args[1], True, "write")
            elif event == "shutil.rmtree":
                _check_path(args[0], True, "delete")
            elif event in ("os.listdir", "os.scandir"):
                if args and args[0] is not None:
                    _check_path(args[0], False, "read")
            elif event == "socket.connect" and not _allow_network:
                _record("network", "socket.connect", False)
                raise PermissionError("MolaGPT 沙盒：网络未启用")

        if _enforce:
            sys.addaudithook(_hook)

        workspace = os.getcwd()
        packages = os.path.join(workspace, ".packages")
        # -I deliberately ignores inherited PYTHONPATH and user site packages.
        # Add only the current conversation workspace and its controlled package
        # overlay back to sys.path.
        if workspace not in sys.path:
            sys.path.insert(0, workspace)
        if os.path.isdir(packages):
            site.addsitedir(packages)
            if packages in sys.path:
                sys.path.remove(packages)
            sys.path.insert(0, packages)

        try:
            import matplotlib
            matplotlib.use("Agg", force=True)
            import matplotlib.pyplot as plt
            matplotlib.rcParams["font.sans-serif"] = [
                "Microsoft YaHei",
                "SimHei",
                "Noto Sans CJK SC",
                "Source Han Sans SC",
                "Arial Unicode MS",
                "DejaVu Sans",
            ]
            matplotlib.rcParams["axes.unicode_minus"] = False
        except Exception:
            pass

        runpy.run_path(os.environ.get("MOLAGPT_MAIN_SCRIPT") or "main.py", run_name="__main__")
        """;

    /// <summary>
    /// Resolves the working directory for a run. When a conversation id is
    /// available, all Python runs in that conversation share one directory so
    /// files produced by an earlier step (downloaded images, generated charts)
    /// are still present for later steps — this is what removes the model's need
    /// to <c>shutil.copy</c> artifacts between per-run sandboxes. Falls back to a
    /// fresh timestamped directory when there is no conversation id.
    /// </summary>
    /// <summary>
    /// Public accessor for a conversation's Python working directory — the same
    /// folder <see cref="ToolName"/> runs in and where uploaded files and
    /// generated artifacts live. Returns the path without creating it; callers
    /// that only scan should check <see cref="Directory.Exists(string)"/> first.
    /// </summary>
    public static string GetSessionDirectory(string? conversationId) =>
        ResolveSessionDirectory(conversationId);

    /// <summary>Names of the runtime scaffolding scripts written into the session
    /// directory; artifact scanners exclude these.</summary>
    public static IReadOnlyCollection<string> RuntimeScriptFileNames { get; } =
        new[]
        {
            UserScriptFileName, RunnerScriptFileName,
            StdoutOverflowFileName, StderrOverflowFileName, SandboxReportFileName
        };

    private static string ResolveSessionDirectory(string? conversationId)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT",
            "python-tool",
            "sessions");

        var slug = SanitizeConversationId(conversationId);
        var leaf = slug is null
            ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]
            : "conv-" + slug;

        return Path.Combine(root, leaf);
    }

    /// <summary>
    /// Copies a user-attached file into the per-conversation Python workspace so
    /// the model can read it later via <see cref="ToolName"/> using a plain
    /// relative path. Mirrors <see cref="ResolveSessionDirectory"/> so the copied
    /// file lands in the very directory each <c>execute_python_code</c> run uses
    /// as its working directory. Returns the workspace-relative path (the file
    /// name), which is what the model should pass to <c>open()</c>.
    /// </summary>
    public static string CopyAttachmentToSession(string? conversationId, string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var sessionDir = ResolveSessionDirectory(conversationId);
        Directory.CreateDirectory(sessionDir);

        var safeName = SanitizeAttachmentFileName(fileName);
        var destination = EnsureUniquePath(sessionDir, safeName);
        File.WriteAllBytes(destination, bytes);
        ct.ThrowIfCancellationRequested();
        return Path.GetFileName(destination);
    }

    /// <summary>Strips directory components and illegal characters from an
    /// attachment file name, falling back to a generic name when nothing usable
    /// remains, so a malicious or empty name can never escape the workspace.</summary>
    private static string SanitizeAttachmentFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return "attachment-" + Guid.NewGuid().ToString("N")[..8];

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var result = builder.ToString().Trim().Trim('.');
        return string.IsNullOrEmpty(result)
            ? "attachment-" + Guid.NewGuid().ToString("N")[..8]
            : result;
    }

    /// <summary>Appends a numeric suffix when a same-named file already exists in
    /// the (reused) session directory so a new upload never clobbers an earlier
    /// one within the same conversation.</summary>
    private static string EnsureUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem}-{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    /// <summary>
    /// Maps a conversation id to a safe directory-name fragment. Keeps only
    /// filename-safe characters; ids that are empty, become empty after
    /// filtering, or exceed a length bound fall back to a SHA-256 prefix (or
    /// null for empty, which triggers the timestamped path).
    /// </summary>
    private static string? SanitizeConversationId(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var trimmed = conversationId.Trim();
        var safe = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                safe.Append(ch);
            else
                safe.Append('_');
        }

        var result = safe.ToString().Trim('_');
        if (result.Length == 0)
            return HashConversationId(trimmed);

        // Bound the length to keep total paths well under MAX_PATH; hash long ids.
        return result.Length <= 64 ? result : HashConversationId(trimmed);
    }

    private static string HashConversationId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static IReadOnlyList<string> SplitPrefixes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads what the audit hook recorded for this run and deletes the file, so
    /// the next run starts clean and the report never shows up as an artifact.
    /// Best effort throughout: a missing or malformed report means the run has
    /// nothing to say about itself, never that the run failed.
    /// </summary>
    private static SandboxReport ReadSandboxReport(string path)
    {
        if (!File.Exists(path)) return SandboxReport.Empty;

        var allowed = new List<object>();
        var denied = new List<object>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var entry = new
                    {
                        kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null,
                        target = root.TryGetProperty("target", out var t) ? t.GetString() : null
                    };
                    var ok = root.TryGetProperty("allowed", out var a) && a.ValueKind == JsonValueKind.True;
                    (ok ? allowed : denied).Add(entry);
                }
                catch (JsonException)
                {
                    // One torn line (a run killed mid-write) is not worth losing
                    // the rest of the report over.
                }
            }
        }
        catch (IOException)
        {
            return SandboxReport.Empty;
        }

        try { File.Delete(path); } catch { /* best effort */ }

        // The allowed list is the long one — every stdlib file an import touched
        // is in it — and the model does not need it enumerated. Denials are the
        // actionable half, so they are never trimmed.
        return new SandboxReport(allowed.Take(24).ToArray(), denied.ToArray());
    }

    private sealed record SandboxReport(IReadOnlyList<object> Allowed, IReadOnlyList<object> Denied)
    {
        public static SandboxReport Empty { get; } =
            new(Array.Empty<object>(), Array.Empty<object>());
    }

    private static IReadOnlyList<PythonArtifact> ScanArtifacts(string sessionDir, DateTime runStartUtc)
    {
        if (!Directory.Exists(sessionDir))
            return Array.Empty<PythonArtifact>();

        var artifacts = new List<PythonArtifact>();
        foreach (var file in PythonWorkspaceInternals.EnumerateUserFiles(sessionDir))
        {
            var relative = Path.GetRelativePath(sessionDir, file);
            var name = Path.GetFileName(file);
            if (!PythonWorkspaceInternals.IsReportableUserFile(sessionDir, file, RuntimeScriptFileNames))
            {
                continue;
            }

            var info = new FileInfo(file);

            // Only report files this run produced or touched. In a reused
            // per-conversation directory this excludes earlier turns' artifacts
            // (so they don't re-surface in display_instructions every call); in a
            // fresh directory every file passes, preserving the old behavior.
            if (info.LastWriteTimeUtc < runStartUtc)
                continue;

            artifacts.Add(new PythonArtifact(
                name,
                relative,
                file,
                ContentTypeFor(info.Extension),
                info.Length,
                info.Length > MaxArtifactBytes));
        }

        return artifacts
            .OrderBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static object BuildDisplayInstructions(IReadOnlyList<PythonArtifact> artifacts)
    {
        var markdownImages = artifacts
            .Where(IsImageArtifact)
            .Select(artifact =>
            {
                var alt = Path.GetFileNameWithoutExtension(artifact.Name);
                if (string.IsNullOrWhiteSpace(alt))
                    alt = "generated image";
                return new
                {
                    name = artifact.Name,
                    relative_path = artifact.RelativePath,
                    markdown = $"![{alt}]({EscapeMarkdownPath(artifact.RelativePath)})"
                };
            })
            .ToArray();

        return new
        {
            final_answer = "If you show generated images in the final assistant reply, use one of markdown_images[].markdown exactly, or use the artifact relative_path exactly in Markdown image syntax. Do not invent external URLs, upload URLs, sandbox URLs, /home/user paths, /output paths, or absolute local paths.",
            markdown_images = markdownImages
        };
    }

    private static bool IsImageArtifact(PythonArtifact artifact) =>
        artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(artifact.ContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static string EscapeMarkdownPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return string.Join("/", normalized
            .Split('/', StringSplitOptions.None)
            .Select(Uri.EscapeDataString));
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".csv" => "text/csv",
        ".tsv" => "text/tab-separated-values",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".yaml" or ".yml" => "application/yaml",
        ".md" => "text/markdown",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".html" or ".htm" => "text/html",
        ".pdf" => "application/pdf",
        ".parquet" => "application/vnd.apache.parquet",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static (string? Code, string? Description, int? TimeoutSeconds, IReadOnlyList<string> Paths) ParseArguments(
        string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return (null, null, null, Array.Empty<string>());

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null, null, Array.Empty<string>());

        return (
            ReadString(root, "code") ?? ReadString(root, "python") ?? ReadString(root, "script"),
            ReadString(root, "description") ?? ReadString(root, "purpose"),
            ReadInt(root, "timeout_seconds") ?? ReadInt(root, "timeout"),
            ReadStringArray(root, "paths"));
    }

    /// <summary>
    /// Reads a declared-paths array, tolerating the single string some models
    /// send instead of a one-element array.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var single = prop.Value.GetString();
                return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single! };
            }

            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                return prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .Take(8)
                    .ToArray();
            }
        }

        return Array.Empty<string>();
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n)) return n;
            // Models sometimes send numbers as strings.
            if (prop.Value.ValueKind == JsonValueKind.String
                && int.TryParse(prop.Value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort. The timeout result still reaches the model/user.
        }
    }

    private static object BuildPermissionMeta(
        PythonExecutionOptions options,
        PythonExecutionRiskAnalysis risk,
        string decision,
        PythonReviewVerdict? review) => new
    {
        mode = options.PermissionMode.ToString(),
        decision,
        review = review is null ? null : new
        {
            approved = review.Approved,
            failed = review.Failed,
            model = review.Model,
            risk = review.Risk,
            authorization = review.Authorization,
            reason = review.Reason
        },
        risk_level = risk.Level.ToString(),
        requires_approval = risk.RequiresApproval,
        blocked = risk.Blocked,
        block_reason = risk.BlockReason,
        imports = risk.Imports,
        flags = risk.Flags.Select(f => new
        {
            code = f.Code,
            severity = f.Severity,
            message = f.Message
        }).ToArray()
    };

    private static string Error(string message, string? workingDirectory = null, object? permission = null) => JsonSerializer.Serialize(new
    {
        success = false,
        source = "local_python",
        error = message,
        working_directory = workingDirectory,
        permission
    }, JsonOptions);

    /// <summary>
    /// Collects a stream into a bounded in-memory copy and keeps its complete
    /// output in a per-run log while the process is active.
    ///
    /// Without the spill, everything past the limit was simply gone: a script that
    /// printed more than the cap left the model holding a prefix and a "truncated"
    /// flag, with no way to reach the rest except running it again differently.
    /// The log is under the conversation's working directory, so read_file and
    /// grep_files can open it while another process writes to it.
    /// </summary>
    private sealed class BoundedTextCollector : IDisposable
    {
        /// <summary>How much of the most recent output is kept for status queries
        /// and the end-of-task report.</summary>
        private const int TailCapacity = 4000;

        private readonly int _maxChars;
        private readonly StringBuilder _builder;
        private readonly StringBuilder _tail = new();
        private readonly string? _overflowPath;
        private readonly object _sync = new();
        private readonly bool _mirror;
        private StreamWriter? _overflow;

        /// <param name="mirror">Write the whole stream to <paramref name="overflowPath"/>
        /// from the first byte so a running task's log stays readable.</param>
        public BoundedTextCollector(int maxChars, string? overflowPath = null, bool mirror = false)
        {
            _maxChars = maxChars;
            _builder = new StringBuilder(Math.Min(maxChars, 4096));
            _overflowPath = overflowPath;
            _mirror = mirror;
            if (mirror) BeginOverflow();
        }

        public bool Truncated { get; private set; }

        public string Text
        {
            get { lock (_sync) return _builder.ToString(); }
        }

        /// <summary>The last few thousand characters, readable from any thread while
        /// the process is still writing.</summary>
        public string Tail()
        {
            lock (_sync) return _tail.ToString();
        }

        /// <summary>Path of the complete output, or null when nothing was cut.</summary>
        public string? OverflowPath { get; private set; }

        public void AppendLine(string? line)
        {
            if (line is null)
                return;
            lock (_sync)
            {
                Append(line);
                Append(Environment.NewLine);
            }
        }

        private void Append(string text)
        {
            if (text.Length == 0) return;

            _tail.Append(text);
            if (_tail.Length > TailCapacity) _tail.Remove(0, _tail.Length - TailCapacity);

            if (Truncated)
            {
                _overflow?.Write(text);
                return;
            }

            var remaining = _maxChars - _builder.Length;
            if (text.Length <= remaining)
            {
                _builder.Append(text);
                _overflow?.Write(text); // only open here when mirroring
                return;
            }

            // First overflow: start the file with everything kept so far, so it is
            // the complete stream rather than only the tail. Opened before this
            // text joins the in-memory copy, or its first part would be written twice.
            Truncated = true;
            if (_overflow is null) BeginOverflow();
            if (remaining > 0) _builder.Append(text.AsSpan(0, remaining));
            _overflow?.Write(text);
        }

        private void BeginOverflow()
        {
            if (_overflowPath is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_overflowPath)!);
                _overflow = new StreamWriter(_overflowPath, append: false, new UTF8Encoding(false)) { AutoFlush = _mirror };
                _overflow.Write(_builder.ToString());
                OverflowPath = _overflowPath;
            }
            catch (IOException)
            {
                // A working directory we cannot write to is not worth failing the
                // run over — the inline copy is still returned.
                _overflow = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { _overflow?.Flush(); _overflow?.Dispose(); }
                catch { /* best effort */ }
                _overflow = null;
            }
        }
    }

    private sealed record PythonCandidate(
        string FileName,
        IReadOnlyList<string> PrefixArguments,
        string DisplayName);

    private sealed record PythonRunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated,
        bool TimedOut,
        string? StdoutFullFile = null,
        string? StderrFullFile = null);

    private sealed record PermissionDecision(bool Approved, string Reason, PythonReviewVerdict? Review = null);

    private sealed record PythonArtifact(
        string Name,
        string RelativePath,
        string Path,
        string ContentType,
        long Bytes,
        bool Truncated);
}

/// <summary>What a Python call may do beyond a plain foreground run.</summary>
/// <param name="Owner">The conversation and turn a background task would belong to.</param>
/// <param name="AllowBackground">May run in, or time out into, the background.</param>
/// <param name="Unattended">Nobody is there to approve anything — a sub-agent. A
/// run that would need a decision is refused instead of asked.</param>
/// <param name="Review">What automatic review is shown of the turn. Null leaves
/// every decision to the user.</param>
public sealed record PythonRunContext(
    AgentTaskOwner? Owner,
    bool AllowBackground,
    bool Unattended,
    PythonReviewContext? Review = null);
