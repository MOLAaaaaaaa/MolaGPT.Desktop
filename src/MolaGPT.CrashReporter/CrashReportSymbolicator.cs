using Microsoft.Diagnostics.Runtime;

namespace MolaGPT.CrashReporter;

internal static class CrashReportSymbolicator
{
    public static SymbolicationResult Analyze(string dumpPath, string? symbolPath, string? dacPath = null)
    {
        // Symbol resolution never runs during capture. Network access requires the caller
        // to provide a symbol-server path explicitly; do not inherit _NT_SYMBOL_PATH.
        symbolPath = string.IsNullOrWhiteSpace(symbolPath) ? AppContext.BaseDirectory : symbolPath;
        using var target = DataTarget.LoadDump(dumpPath, new DataTargetOptions
        {
            FileLocator = new CrashSymbolLocator(symbolPath)
        });
        var warnings = new List<string>();
        using var native = NativeSymbolSession.TryCreate(target, symbolPath, warnings);
        using var pdbs = new PortablePdbResolver(symbolPath);
        var threads = new Dictionary<uint, SymbolicatedThread>();
        foreach (var clr in target.ClrVersions)
        {
            try
            {
                using var runtime = string.IsNullOrWhiteSpace(dacPath) ? clr.CreateRuntime() : clr.CreateRuntime(dacPath);
                foreach (var thread in runtime.Threads)
                {
                    var frames = new List<SymbolicatedFrame>();
                    try
                    {
                        foreach (var frame in thread.EnumerateStackTrace(false, 512))
                        {
                            var address = frame.InstructionPointer;
                            var symbol = native?.Resolve(address);
                            var method = frame.Method;
                            var location = method is null ? null : pdbs.Resolve(method, address);
                            frames.Add(new SymbolicatedFrame($"0x{address:X}", frame.Kind.ToString(),
                                method?.Signature ?? frame.FrameName ?? symbol?.Symbol,
                                method?.Type?.Module.Name ?? symbol?.Module, symbol?.Displacement,
                                location?.File ?? symbol?.SourceFile, location?.Line ?? symbol?.Line));
                        }
                    }
                    catch (Exception ex) { warnings.Add($"Managed thread {thread.OSThreadId}: {ex.Message}"); }
                    threads[thread.OSThreadId] = new SymbolicatedThread(thread.OSThreadId, thread.ManagedThreadId, frames, []);
                }
            }
            catch (Exception ex) { warnings.Add($"CLR {clr.Version}: {ex.Message}"); }
        }
        if (native is not null)
        {
            foreach (var threadId in native.ThreadIds)
            {
                var frames = new List<SymbolicatedFrame>();
                try
                {
                    foreach (var address in native.Walk(threadId))
                    {
                        var symbol = native.Resolve(address);
                        frames.Add(new SymbolicatedFrame($"0x{address:X}", "Native", symbol.Symbol,
                            symbol.Module, symbol.Displacement, symbol.SourceFile, symbol.Line));
                    }
                }
                catch (Exception ex) { warnings.Add($"Native thread {threadId}: {ex.Message}"); }
                if (!threads.TryGetValue(threadId, out var thread)) thread = new(threadId, null, [], []);
                threads[threadId] = thread with { NativeFrames = frames };
            }
        }
        var modules = target.EnumerateModules().Select(module => new SymbolicatedModule(
            module.FileName, $"0x{module.ImageBase:X}", module.ImageSize,
            module.Pdb?.Path, module.Pdb?.Guid, module.Pdb?.Revision)).ToArray();
        var frameCount = threads.Values.Sum(thread => thread.ManagedFrames.Count + thread.NativeFrames.Count);
        if (frameCount == 0) warnings.Add("No stack frames could be recovered. Provide the matching runtime DAC and binaries/symbols on the matching OS/architecture.");
        return new SymbolicationResult(1, DateTimeOffset.UtcNow, Path.GetFileName(dumpPath), frameCount > 0,
            threads.Values.ToArray(), modules, warnings);
    }
}

internal sealed record SymbolicatedFrame(string InstructionPointer, string Kind, string? Method, string? Module,
    ulong? Displacement, string? SourceFile, int? Line);
internal sealed record SymbolicatedThread(uint OsThreadId, int? ManagedThreadId,
    IReadOnlyList<SymbolicatedFrame> ManagedFrames, IReadOnlyList<SymbolicatedFrame> NativeFrames);
internal sealed record SymbolicatedModule(string FileName, string BaseAddress, long Size, string? PdbPath, Guid? PdbGuid, int? PdbAge);
internal sealed record SymbolicationResult(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string DumpFileName,
    bool HasStackFrames, IReadOnlyList<SymbolicatedThread> Threads, IReadOnlyList<SymbolicatedModule> Modules,
    IReadOnlyList<string> Warnings);
