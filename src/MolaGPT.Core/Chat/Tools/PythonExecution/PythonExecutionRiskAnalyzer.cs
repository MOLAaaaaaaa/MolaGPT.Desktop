using System.Text.RegularExpressions;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

public enum PythonRiskLevel
{
    Low,
    Medium,
    High,
    Blocked
}

public sealed record PythonRiskFlag(
    string Code,
    string Severity,
    string Message);

public sealed record PythonExecutionRiskAnalysis(
    PythonRiskLevel Level,
    IReadOnlyList<PythonRiskFlag> Flags,
    IReadOnlyList<string> Imports,
    bool RequiresApproval,
    bool HardDenied,
    string? BlockReason,
    IReadOnlyList<string> LiteralPaths)
{
    /// <summary>True when nothing risky was found: the deny layer is clear and
    /// no flag asks for approval, so the call can be auto-approved.</summary>
    public bool AutoApprovable => !HardDenied && !RequiresApproval;

    /// <summary>Back-compat alias: a hard deny is the only unconditional block.</summary>
    public bool Blocked => HardDenied;

    public string Summary =>
        Flags.Count == 0
            ? "未发现明显高风险操作。"
            : string.Join("；", Flags.Take(6).Select(f => f.Message));
}

public static partial class PythonExecutionRiskAnalyzer
{
    private static readonly string[] DefaultAllowedImports =
    [
        "array", "base64", "cmath", "collections", "csv", "datetime", "decimal", "fractions",
        "functools", "itertools", "json", "math", "matplotlib", "numpy", "openpyxl", "pandas",
        "PIL", "random", "re", "scipy", "seaborn", "statistics", "string", "sympy", "textwrap",
        "time", "typing", "uuid"
    ];

    private static readonly string[] BuiltInRestrictedImports =
    [
        "ctypes", "subprocess", "winreg"
    ];

    private static readonly string[] NetworkImports =
    [
        "aiohttp", "boto3", "botocore", "ftplib", "http.client", "httpx", "imaplib", "paramiko",
        "poplib", "requests", "smtplib", "socket", "ssl", "urllib", "urllib.request", "websocket"
    ];

    private static readonly string[] SensitiveImports =
    [
        "getpass", "keyring", "os", "pathlib", "shutil", "site", "sys"
    ];

    /// <summary>True when a module is in the built-in known-safe import list, so
    /// the approval UI can skip offering it as a candidate allow rule.</summary>
    public static bool IsDefaultAllowedImport(string module) =>
        !string.IsNullOrWhiteSpace(module) && MatchesModuleList(module, DefaultAllowedImports);

    public static PythonExecutionRiskAnalysis Analyze(string code, PythonExecutionOptions? options)
    {
        options ??= new PythonExecutionOptions();
        var flags = new List<PythonRiskFlag>();
        var imports = ExtractImports(code).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var deniedImports = SplitList(options.DeniedImports);
        var allowedImports = MergeLists(DefaultAllowedImports, options.AllowedImports);
        var allowedPathPrefixes = SplitList(options.AllowedPathPrefixes);
        var deniedPathPrefixes = SplitList(options.DeniedPathPrefixes);

        var blocked = false;
        string? blockReason = null;
        var requiresApproval = false;

        // Mode-agnostic fact finding. Each finding carries a severity; the caller
        // (ResolvePermissionAsync) turns these facts into a layered decision:
        //   critical (block:true) -> hard deny, even under full access
        //   high/medium (ask:true) -> needs approval
        //   low                    -> auto-approvable
        void Add(string codeValue, string severity, string message, bool ask = true, bool block = false)
        {
            flags.Add(new PythonRiskFlag(codeValue, severity, message));
            if (ask)
                requiresApproval = true;
            if (block && !blocked)
            {
                blocked = true;
                blockReason = message;
            }
        }

        foreach (var module in imports)
        {
            // User denylist is a hard deny in every mode (deny always wins).
            if (MatchesModuleList(module, deniedImports))
            {
                Add("denied_import", "critical", $"用户规则禁止导入模块 {module}", block: true);
                continue;
            }

            // Network is gated by the explicit AllowNetwork switch: when off, any
            // network import is a hard deny — and this is checked before the
            // allow list, so the switch cannot be bypassed by allowing the module.
            if (!options.AllowNetwork && MatchesModuleList(module, NetworkImports))
            {
                Add("network_import", "critical", $"当前未允许网络，禁止导入网络模块 {module}", block: true);
                continue;
            }

            // A module the user explicitly added to the allow list (including
            // session-granted ones merged in upstream) is trusted: skip the
            // restricted/network/system/unknown approval flags below. The user
            // denylist and the network switch above still take precedence.
            if (MatchesModuleList(module, allowedImports))
                continue;

            // OS-reachable modules always need approval but are not auto-blocked;
            // put them on the denylist to hard-block.
            if (MatchesModuleList(module, BuiltInRestrictedImports))
            {
                Add("restricted_import", "high", $"代码导入可操作系统资源的高风险模块 {module}");
                continue;
            }

            if (MatchesModuleList(module, NetworkImports))
                Add("network_import", "high", $"代码导入网络模块 {module}");
            else if (MatchesModuleList(module, SensitiveImports))
                Add("system_import", "medium", $"代码导入可能访问系统资源的模块 {module}");

            // Anything outside the known-safe libraries (and not already flagged
            // as network/system above) is surfaced for approval, but not blocked.
            if (!MatchesModuleList(module, SensitiveImports)
                && !MatchesModuleList(module, NetworkImports))
            {
                Add("unknown_import", "medium", $"模块 {module} 不在已知安全导入列表中");
            }
        }

        if (CommandExecutionPattern().IsMatch(code))
            Add("process_execution", "high", "代码可能启动系统命令或子进程");

        if (PackageInstallPattern().IsMatch(code))
            Add("package_install", "high", "代码可能安装或修改 Python 包");

        if (!options.AllowNetwork && NetworkCallPattern().IsMatch(code))
            Add("network_call", "critical", "当前未允许网络，代码可能访问网络", block: true);

        if (DestructiveFilePattern().IsMatch(code))
            Add("destructive_file", "high", "代码可能删除、移动或覆盖文件");

        if (EnvironmentAccessPattern().IsMatch(code))
            Add("environment_access", "high", "代码可能读取环境变量或用户敏感目录");

        if (DynamicExecutionPattern().IsMatch(code))
            Add("dynamic_execution", "high", "代码使用动态执行或动态导入");

        var literalPaths = ExtractLiteralPaths(code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var literalPath in literalPaths)
        {
            // Denylist wins.
            if (PathMatchesAnyPrefix(literalPath, deniedPathPrefixes))
            {
                Add("denied_path", "critical", $"规则禁止访问路径 {literalPath}", block: true);
                continue;
            }

            // A path under an explicitly allowed prefix is trusted: no flag at
            // all (this is what makes "allow this path" actually stop asking).
            if (PathMatchesAnyPrefix(literalPath, allowedPathPrefixes))
                continue;

            // Outside an active allowlist is higher risk than a bare absolute path.
            if (allowedPathPrefixes.Length > 0)
                Add("outside_allowed_path", "high", $"代码引用了未在允许列表中的路径 {literalPath}");
            else
                Add("absolute_path", "medium", $"代码引用了本机绝对路径 {literalPath}");
        }

        var level = blocked
            ? PythonRiskLevel.Blocked
            : flags.Any(f => string.Equals(f.Severity, "high", StringComparison.OrdinalIgnoreCase))
                ? PythonRiskLevel.High
                : flags.Any(f => string.Equals(f.Severity, "medium", StringComparison.OrdinalIgnoreCase))
                    ? PythonRiskLevel.Medium
                    : PythonRiskLevel.Low;

        return new PythonExecutionRiskAnalysis(level, flags, imports, requiresApproval, blocked, blockReason, literalPaths);
    }

    private static IReadOnlyList<string> ExtractImports(string code)
    {
        var modules = new List<string>();
        foreach (Match match in ImportPattern().Matches(code))
        {
            var raw = match.Groups["mods"].Value;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name))
                    modules.Add(RootModule(name));
            }
        }

        foreach (Match match in FromImportPattern().Matches(code))
        {
            var name = match.Groups["mod"].Value;
            if (!string.IsNullOrWhiteSpace(name))
                modules.Add(RootModule(name));
        }

        return modules;
    }

    private static IEnumerable<string> ExtractLiteralPaths(string code)
    {
        foreach (Match match in LiteralPathPattern().Matches(code))
        {
            var value = match.Groups["path"].Value;
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static string RootModule(string module)
    {
        var trimmed = module.Trim();
        return trimmed.StartsWith("PIL.", StringComparison.OrdinalIgnoreCase)
            ? "PIL"
            : trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
    }

    private static string[] MergeLists(IEnumerable<string> defaults, string? userValues) =>
        defaults.Concat(SplitList(userValues))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesModuleList(string module, IEnumerable<string> list) =>
        list.Any(item =>
            string.Equals(module, item, StringComparison.OrdinalIgnoreCase)
            || module.StartsWith(item + ".", StringComparison.OrdinalIgnoreCase));

    private static bool PathMatchesAnyPrefix(string path, IEnumerable<string> prefixes)
    {
        var normalized = path.Replace('/', '\\').Trim();
        return prefixes.Any(prefix =>
        {
            var p = prefix.Replace('/', '\\').Trim().TrimEnd('\\');
            return p.Length > 0
                   && (normalized.Equals(p, StringComparison.OrdinalIgnoreCase)
                       || normalized.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));
        });
    }

    [GeneratedRegex(@"(?m)^\s*import\s+(?<mods>[^\r\n#]+)")]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"(?m)^\s*from\s+(?<mod>[A-Za-z_][\w.]*)\s+import\s+")]
    private static partial Regex FromImportPattern();

    [GeneratedRegex(@"\b(subprocess\.[A-Za-z_]+|os\.(system|popen|spawn\w*|startfile|exec\w*)|startfile\s*\()")]
    private static partial Regex CommandExecutionPattern();

    [GeneratedRegex(@"\b(pip\s+install|python\s+-m\s+pip|ensurepip|subprocess\.[A-Za-z_]+\([^)]*pip)")]
    private static partial Regex PackageInstallPattern();

    [GeneratedRegex(@"\b(requests\.|urllib\.request|httpx\.|aiohttp\.|socket\.|ftplib\.|smtplib\.)")]
    private static partial Regex NetworkCallPattern();

    [GeneratedRegex(@"\b(os\.(remove|unlink|rmdir|removedirs|rename|replace)|shutil\.(rmtree|move)|Path\([^)]*\)\.(unlink|rmdir|rename|replace))\s*\(")]
    private static partial Regex DestructiveFilePattern();

    [GeneratedRegex(@"\b(os\.environ|os\.getenv|expanduser|getpass\.|keyring\.|\.ssh|AppData|USERPROFILE|HOME)")]
    private static partial Regex EnvironmentAccessPattern();

    [GeneratedRegex(@"\b(eval|exec|compile|__import__)\s*\(|importlib\.")]
    private static partial Regex DynamicExecutionPattern();

    [GeneratedRegex(@"(?<quote>['""])(?<path>(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|%[A-Za-z_][A-Za-z0-9_]*%[\\/])[^'""]+)\k<quote>")]
    private static partial Regex LiteralPathPattern();
}
