using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

public sealed class PythonExecutionTool
{
    public const string ToolName = "execute_python_code";

    private const string UserScriptFileName = "main.py";
    private const string RunnerScriptFileName = "runner.py";
    private const long MaxArtifactBytes = 50L * 1024L * 1024L;

    // Timestamp skew applied when deciding which files a run produced. Absorbs
    // filesystem mtime granularity (FAT/exFAT is 2s) plus minor clock jitter so
    // a freshly written artifact is never excluded as "too old".
    private static readonly TimeSpan ArtifactFreshnessSkew = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IPythonExecutionApprovalService? _approval;
    private readonly IPythonSessionAllowList? _sessionAllowList;

    public PythonExecutionTool(
        IPythonExecutionApprovalService? approval = null,
        IPythonSessionAllowList? sessionAllowList = null)
    {
        _approval = approval;
        _sessionAllowList = sessionAllowList;
    }

    public static object BuildOpenAiToolDefinition(PythonExecutionOptions options)
    {
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
                description = "Run Python code locally on the user's computer. This is a general-purpose local execution tool, similar to a bash/shell: prefer it whenever a task is better done by running code than by answering from memory. "
                    + "Use it for, but not limited to: math and data analysis; reading, writing, creating, moving and inspecting local files and folders; converting or generating documents, spreadsheets, images and plots; inspecting the system and environment; automating multi-step local tasks; and calling operating-system facilities via the standard library (e.g. os, pathlib, shutil, subprocess) when the task needs them. "
                    + "It runs real Python on the local machine with the user's privileges and a persistent filesystem, not a throwaway image-analysis sandbox. "
                    + "Within one conversation, every call shares the SAME working directory: files you create in one call (downloaded images, generated charts, data files) are still there in later calls under the same name. Read and write using normal relative paths in the current working directory, and reuse files from earlier steps directly — do NOT copy them from other directories or hard-code full filesystem locations from previous runs. "
                    + "Print results and short progress to stdout so the user and you can see them. "
                    + "When you produce plots or images, save them as PNG/JPG files in the current working directory; matplotlib is set up for headless rendering with common Chinese fonts when available. "
                    + "After execution, follow the returned display_instructions: show images with the exact artifact relative_path in Markdown, and never invent external image URLs or local file-system locations. "
                    + $"Current execution permission mode is {options.PermissionMode}; risky actions may require the user to approve them before they run. "
                    + (requiresPurpose
                        ? "Approval mode is active: always provide the `description` argument, because the user must read it and approve before the code runs. "
                        : string.Empty)
                    + (options.AllowNetwork
                        ? "Network access is allowed by the user's tool settings."
                        : "Network access is disabled by default; do not rely on downloading packages or fetching URLs unless the user explicitly enables network."),
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
                        }
                    },
                    required = requiresPurpose ? new[] { "code", "description" } : new[] { "code" }
                }
            }
        };
    }

    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PythonExecutionOptions? options,
        string? conversationId,
        CancellationToken ct)
    {
        if (options?.Enabled != true)
            return Error("Python tool is not enabled.");

        var (code, description) = ParseArguments(argumentsJson);
        if (string.IsNullOrWhiteSpace(code))
            return Error("A non-empty Python code string is required.");

        // Fold any session-scoped allow rules (granted earlier in this run via
        // the approval dialog) into the options BEFORE analysis, so the analyzer
        // sees the allowed modules/paths as known-safe and auto-approves them.
        var effectiveOptions = MergeSessionAllowList(options, _sessionAllowList);

        var risk = PythonExecutionRiskAnalyzer.Analyze(code!, effectiveOptions);
        var permission = await ResolvePermissionAsync(code!, description, effectiveOptions, risk, ct).ConfigureAwait(false);
        if (!permission.Approved)
        {
            return Error(
                "Python 执行已被权限策略拒绝：" + permission.Reason,
                permission: BuildPermissionMeta(effectiveOptions, risk, "denied"));
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300));
        var maxOutput = Math.Clamp(options.MaxOutputCharacters, 2000, 100000);
        var sessionDir = ResolveSessionDirectory(conversationId);

        try
        {
            Directory.CreateDirectory(sessionDir);
            var userScriptPath = Path.Combine(sessionDir, UserScriptFileName);
            var runnerScriptPath = Path.Combine(sessionDir, RunnerScriptFileName);
            await File.WriteAllTextAsync(userScriptPath, code!, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(runnerScriptPath, BuildRunnerScript(), new UTF8Encoding(false), ct).ConfigureAwait(false);

            var python = await ResolvePythonAsync(options, ct).ConfigureAwait(false);

            // Capture the run-start instant AFTER writing the scripts but BEFORE
            // running, so ScanArtifacts can report only files this run created or
            // modified. The small skew absorbs filesystem timestamp granularity
            // and clock jitter. This is what keeps a reused (per-conversation)
            // working directory from re-reporting every earlier turn's images.
            var runStartUtc = DateTime.UtcNow - ArtifactFreshnessSkew;
            var startedAt = Stopwatch.StartNew();
            var run = await RunPythonAsync(
                python,
                runnerScriptPath,
                sessionDir,
                timeout,
                maxOutput,
                options.AllowNetwork,
                ct).ConfigureAwait(false);
            startedAt.Stop();

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
                success = !run.TimedOut && run.ExitCode == 0,
                source = "local_python",
                description,
                python = python.DisplayName,
                working_directory = sessionDir,
                permission = BuildPermissionMeta(effectiveOptions, risk, "approved"),
                artifacts,
                display_instructions = BuildDisplayInstructions(scannedArtifacts),
                stdout = run.Stdout,
                stderr = run.Stderr,
                stdout_truncated = run.StdoutTruncated,
                stderr_truncated = run.StderrTruncated,
                exit_code = run.ExitCode,
                duration_ms = (long)startedAt.Elapsed.TotalMilliseconds,
                timed_out = run.TimedOut
            }, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message, sessionDir);
        }
    }

    private async Task<PermissionDecision> ResolvePermissionAsync(
        string code,
        string? description,
        PythonExecutionOptions options,
        PythonExecutionRiskAnalysis risk,
        CancellationToken ct)
    {
        // Layered permission filter (deny -> full-access -> auto-allow -> ask),
        // the model the industry converged on (Claude Code / Cursor / Windsurf).
        // The rules are no longer a separate mode; they are a filter that always
        // sits on top of the approval flow.

        // [1] Deny layer: hard-denied code is rejected even under full access.
        if (risk.HardDenied)
            return new PermissionDecision(false, risk.BlockReason ?? "已被拒绝规则拦截。");

        // Package installation always needs a separate, explicit decision. It
        // persists into this conversation's .packages directory and therefore
        // must not be hidden by FullAccess or a remembered import/path rule.
        if (risk.Flags.Any(flag => string.Equals(flag.Code, "package_install", StringComparison.Ordinal)))
        {
            if (_approval is null)
                return new PermissionDecision(false, "Python 包安装需要用户审批，但当前没有可用的审批服务。");

            var installDecision = await _approval.RequestApprovalAsync(
                new PythonExecutionApprovalRequest(code, description, options, risk, BuildCapabilities(options, risk)),
                ct).ConfigureAwait(false);
            return installDecision == PythonExecutionApprovalDecision.Approved
                ? new PermissionDecision(true, "用户已批准将包安装到当前对话环境。")
                : new PermissionDecision(false, "用户已拒绝 Python 包安装。");
        }

        // Destructive filesystem operations stay reviewable even when the user
        // selected FullAccess, matching the global tool policy.
        if (risk.Flags.Any(flag => string.Equals(flag.Code, "destructive_file", StringComparison.Ordinal)))
        {
            if (_approval is null)
                return new PermissionDecision(false, "破坏性文件操作需要用户审批，但当前没有可用的审批服务。");

            var destructiveDecision = await _approval.RequestApprovalAsync(
                new PythonExecutionApprovalRequest(code, description, options, risk, BuildCapabilities(options, risk)),
                ct).ConfigureAwait(false);
            return destructiveDecision == PythonExecutionApprovalDecision.Approved
                ? new PermissionDecision(true, "用户已批准破坏性文件操作。")
                : new PermissionDecision(false, "用户已拒绝破坏性文件操作。");
        }

        // [2] Full access: trust everything that survived the deny layer.
        if (options.PermissionMode == PythonPermissionMode.FullAccess)
            return new PermissionDecision(true, "完全权限模式已允许执行。");

        // [3] Allow layer: nothing risky found -> auto-approve without a prompt.
        if (risk.AutoApprovable)
            return new PermissionDecision(true, "未发现需要审批的风险，已自动放行。");

        // [4] Everything else needs an explicit user decision.
        if (_approval is null)
            return new PermissionDecision(false, "需要用户审批，但当前没有可用的审批服务。");

        var decision = await _approval.RequestApprovalAsync(
            new PythonExecutionApprovalRequest(code, description, options, risk, BuildCapabilities(options, risk)),
            ct).ConfigureAwait(false);
        return decision == PythonExecutionApprovalDecision.Approved
            ? new PermissionDecision(true, "用户已批准。")
            : new PermissionDecision(false, "用户已拒绝。");
    }

    private static PythonExecutionOptions MergeSessionAllowList(
        PythonExecutionOptions options,
        IPythonSessionAllowList? sessionAllowList)
    {
        if (sessionAllowList is null)
            return options;

        var imports = sessionAllowList.Imports;
        var pathPrefixes = sessionAllowList.PathPrefixes;
        if (imports.Count == 0 && pathPrefixes.Count == 0)
            return options;

        return options with
        {
            AllowedImports = AppendList(options.AllowedImports, imports),
            AllowedPathPrefixes = AppendList(options.AllowedPathPrefixes, pathPrefixes)
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
        throw new InvalidOperationException("找不到已配置的 Python。请在设置中一键配置 MolaGPT 专用环境，或明确选择一个 python.exe。不会回退到系统 Python。" + detail);
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

    private static async Task<PythonRunResult> RunPythonAsync(
        PythonCandidate candidate,
        string runnerScriptPath,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputCharacters,
        bool allowNetwork,
        CancellationToken ct)
    {
        using var process = CreateProcess(candidate, new[] { "-I", "-X", "utf8", "-u", runnerScriptPath }, workingDirectory, allowNetwork);
        var stdout = new BoundedTextCollector(maxOutputCharacters);
        var stderr = new BoundedTextCollector(maxOutputCharacters);
        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        if (!process.Start())
            throw new InvalidOperationException("Python process failed to start.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        process.WaitForExit();
        return new PythonRunResult(
            process.ExitCode,
            stdout.Text,
            stderr.Text,
            stdout.Truncated,
            stderr.Truncated,
            timedOut);
    }

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
        process.StartInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            new[] { pythonDirectory, scriptsDirectory, systemDirectory }
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
            process.StartInfo.Environment["HOME"] = workingDirectory;
            process.StartInfo.Environment["USERPROFILE"] = workingDirectory;
            process.StartInfo.Environment["APPDATA"] = appDataDirectory;
            process.StartInfo.Environment["LOCALAPPDATA"] = localAppDataDirectory;
            process.StartInfo.Environment["PIP_TARGET"] = packageDirectory;
            process.StartInfo.Environment["PIP_CACHE_DIR"] = pipCacheDirectory;
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
        import os
        import runpy
        import site
        import sys

        os.environ.setdefault("PYTHONIOENCODING", "utf-8")
        os.environ.setdefault("PYTHONUTF8", "1")
        os.environ.setdefault("MPLBACKEND", "Agg")

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

        runpy.run_path("main.py", run_name="__main__")
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
        new[] { UserScriptFileName, RunnerScriptFileName };

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

    private static (string? Code, string? Description) ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return (null, null);

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null);

        return (
            ReadString(root, "code") ?? ReadString(root, "python") ?? ReadString(root, "script"),
            ReadString(root, "description") ?? ReadString(root, "purpose"));
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

    private static object BuildPermissionMeta(PythonExecutionOptions options, PythonExecutionRiskAnalysis risk, string decision) => new
    {
        mode = options.PermissionMode.ToString(),
        decision,
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

    private sealed class BoundedTextCollector
    {
        private readonly int _maxChars;
        private readonly StringBuilder _builder;

        public BoundedTextCollector(int maxChars)
        {
            _maxChars = maxChars;
            _builder = new StringBuilder(Math.Min(maxChars, 4096));
        }

        public bool Truncated { get; private set; }
        public string Text => _builder.ToString();

        public void AppendLine(string? line)
        {
            if (line is null)
                return;
            Append(line);
            Append(Environment.NewLine);
        }

        private void Append(string text)
        {
            if (Truncated || text.Length == 0)
                return;

            var remaining = _maxChars - _builder.Length;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            if (text.Length <= remaining)
            {
                _builder.Append(text);
                return;
            }

            _builder.Append(text.AsSpan(0, remaining));
            Truncated = true;
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
        bool TimedOut);

    private sealed record PermissionDecision(bool Approved, string Reason);

    private sealed record PythonArtifact(
        string Name,
        string RelativePath,
        string Path,
        string ContentType,
        long Bytes,
        bool Truncated);
}
