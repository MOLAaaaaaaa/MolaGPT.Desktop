using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace MolaGPT.CrashReporter;

// ClrMD 4 accepts server URLs in SymbolPaths, whereas DbgHelp uses srv*cache*url.
// Handle ordinary local directories explicitly and pass only explicit servers on.
internal sealed class CrashSymbolLocator : IFileLocator
{
    private readonly string[] _directories;
    private readonly IFileLocator _servers;

    public CrashSymbolLocator(string symbolPath)
    {
        var entries = symbolPath.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _directories = entries.Where(Directory.Exists).Select(Path.GetFullPath).ToArray();
        var servers = entries.SelectMany(entry => entry.StartsWith("srv*", StringComparison.OrdinalIgnoreCase)
                ? entry.Split('*').Skip(1) : [entry])
            .Where(entry => Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme == "https").ToArray();
        _servers = new DataTargetOptions { SymbolPaths = servers }.FileLocator;
    }

    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var candidates = new[] { fileName }.Concat(_directories.Select(directory => Path.Combine(directory, name)));
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var file = File.OpenRead(candidate);
                using var pe = new PEReader(file);
                if (pe.PEHeaders.CoffHeader.TimeDateStamp == buildTimeStamp
                    && pe.PEHeaders.PEHeader?.SizeOfImage == imageSize)
                    return candidate;
            }
            catch { }
        }
        return _servers.FindPEImage(fileName, buildTimeStamp, imageSize, checkProperties);
    }

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID,
        OSPlatform originalPlatform, bool checkProperties) =>
        _servers.FindPEImage(fileName, archivedUnder, buildIdOrUUID, originalPlatform, checkProperties);
}
