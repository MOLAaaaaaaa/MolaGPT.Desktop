using System.Diagnostics;
using System.Globalization;
using System.Text;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.PythonExecution;

namespace MolaGPT.Core.Chat.Tools.Mcp;

/// <summary>
/// A stdio MCP server process, started by MolaGPT rather than by the SDK.
///
/// The SDK ends its server by killing the process tree, but launchers such as
/// <c>npx</c> put short-lived processes between MolaGPT and the server. When the
/// launcher exits on the closed stdin, the server is no longer in the tree, and a
/// server kept alive by its own timers keeps running. A job object holds every
/// process the server starts, and closing it ends them all — also when MolaGPT
/// itself is killed.
/// </summary>
internal sealed class McpStdioProcess : IAsyncDisposable
{
    private const int StderrTailLines = 20;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private readonly WindowsJobObject? _job;
    private readonly Queue<string> _stderr = new();

    private McpStdioProcess(Process process, WindowsJobObject? job)
    {
        _process = process;
        _job = job;
    }

    public Stream Input => _process.StandardInput.BaseStream;
    public Stream Output => _process.StandardOutput.BaseStream;

    public static McpStdioProcess Start(McpServerOptions server, Action<string>? log)
    {
        var command = server.Command?.Trim();
        if (string.IsNullOrEmpty(command)) throw new InvalidOperationException("未设置启动命令。");

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // No BOM: a JSON-RPC server rejects a leading U+FEFF on the first message.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // Not the app's own directory, which is where the process would
            // otherwise start and where relative paths would resolve.
            WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : server.WorkingDirectory,
        };

        // npx, uvx and the like are batch files, which only cmd.exe can run.
        if (OperatingSystem.IsWindows() && !IsExecutable(command))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = command;
        }
        foreach (var argument in server.Arguments ?? []) psi.ArgumentList.Add(argument);
        foreach (var (key, value) in server.Environment ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        var job = OperatingSystem.IsWindows() ? WindowsJobObject.TryCreate(null, null, log) : null;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程。");
        }
        catch
        {
            if (OperatingSystem.IsWindows()) job?.Dispose();
            throw;
        }

        // At once, before the launcher has started anything: children inherit the job.
        if (OperatingSystem.IsWindows()) job?.TryAssign(process, log);

        var started = new McpStdioProcess(process, job);
        _ = Task.Run(() => started.ReadStderrAsync(server.Name, log));
        return started;
    }

    /// <summary>
    /// Line by line, each decoded on its own: the server writes UTF-8, but cmd.exe
    /// reports a missing command in the console code page, and that message is the
    /// one a mistyped command produces.
    /// </summary>
    private async Task ReadStderrAsync(string serverName, Action<string>? log)
    {
        var stream = _process.StandardError.BaseStream;
        var line = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        line.WriteByte(buffer[i]);
                        continue;
                    }
                    Keep(Decode(line), serverName, log);
                    line.SetLength(0);
                }
            }
            if (line.Length > 0) Keep(Decode(line), serverName, log);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }

    private void Keep(string text, string serverName, Action<string>? log)
    {
        text = text.TrimEnd('\r');
        if (text.Length == 0) return;
        lock (_stderr)
        {
            _stderr.Enqueue(text);
            while (_stderr.Count > StderrTailLines) _stderr.Dequeue();
        }
        log?.Invoke($"[mcp:{serverName}] {text}");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string Decode(MemoryStream line)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var console = CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            return (console ?? Encoding.Latin1).GetString(bytes);
        }
    }

    /// <summary>Why the process is gone, with the end of its stderr; null while it runs.</summary>
    public async Task<string?> DescribeExitAsync()
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        string tail;
        lock (_stderr) tail = string.Join("\n", _stderr);
        var reason = $"服务器进程已退出（代码 {_process.ExitCode}）。";
        return tail.Length == 0 ? reason : reason + "\n" + tail;
    }

    private static bool IsExecutable(string command)
    {
        var extension = Path.GetExtension(command);
        if (extension.Length > 0) return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        if (Path.IsPathRooted(command)) return File.Exists(command + ".exe");
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim(), command + ".exe")));
    }

    public async ValueTask DisposeAsync()
    {
        // Closing stdin is the MCP way to ask a stdio server to stop.
        try { _process.StandardInput.Close(); }
        catch (IOException) { /* already gone */ }

        try
        {
            using var grace = new CancellationTokenSource(ExitGrace);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ended below with the rest of the tree.
        }

        if (_job is not null && OperatingSystem.IsWindows()) _job.Dispose();
        else
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* exited meanwhile */ }
        }
        _process.Dispose();
    }
}
