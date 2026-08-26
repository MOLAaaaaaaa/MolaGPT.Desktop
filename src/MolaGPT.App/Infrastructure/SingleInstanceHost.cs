using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Infrastructure;

internal static class SingleInstanceHost
{
    private const string MutexName = "Global\\MolaGPT.Desktop.SingleInstance.v1";
    private const string PipeName = "MolaGPT.Desktop.SingleInstance.v1";
    private const string ActivateMessage = "ACTIVATE";

    private static readonly ConcurrentQueue<InstanceMessage> Pending = new();
    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;
    private static Action<string?>? _receiver;

    public static bool TryAcquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ForwardToFirstInstance(ExtractDeepLink(args));
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        _listenerCts = new CancellationTokenSource();
        _ = ListenAsync(_listenerCts.Token);

        if (ExtractDeepLink(args) is { } initialDeepLink)
            Pending.Enqueue(new InstanceMessage(initialDeepLink));
        return true;
    }

    public static void Attach(Action<string?> receiver)
    {
        _receiver = receiver;
        while (Pending.TryDequeue(out var message)) receiver(message.DeepLink);
    }

    public static string? ExtractDeepLink(IEnumerable<string> args) =>
        args.FirstOrDefault(arg =>
            !string.IsNullOrWhiteSpace(arg)
            && arg.StartsWith(UrlSchemeRegistrar.Scheme + "://", StringComparison.OrdinalIgnoreCase));

    private static void ForwardToFirstInstance(string? deepLink)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(deepLink ?? ActivateMessage);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("SingleInstance", $"forward failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                var message = string.Equals(line, ActivateMessage, StringComparison.Ordinal) ? null : line;
                var receiver = _receiver;
                if (receiver is null)
                    Pending.Enqueue(new InstanceMessage(message));
                else
                    receiver(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("SingleInstance", $"listener failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static void Release()
    {
        _receiver = null;
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;

        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
    }

    private readonly record struct InstanceMessage(string? DeepLink);
}
