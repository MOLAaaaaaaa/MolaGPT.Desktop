using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.Diagnostics.Runtime;

namespace MolaGPT.CrashReporter;

internal sealed class PortablePdbResolver(string symbolPath) : IDisposable
{
    private readonly Dictionary<Guid, MetadataReaderProvider?> _readers = [];
    private readonly string[] _directories = symbolPath.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(Directory.Exists).ToArray();

    public SourceLocation? Resolve(ClrMethod method, ulong instructionPointer)
    {
        try
        {
            var pdb = method.Type?.Module.Pdb;
            if (pdb is null) return null;
            var moduleName = method.Type?.Module.Name;
            var ilOffset = method.GetILOffset(instructionPointer);
            if (ilOffset < 0) return null;
            var reader = GetReader(pdb, moduleName);
            return reader is null ? null : FindSequencePoint(reader, method.MetadataToken, ilOffset);
        }
        catch { return null; }
    }

    internal static SourceLocation? FindSequencePoint(MetadataReader reader, int methodToken, int ilOffset)
    {
        if ((methodToken & unchecked((int)0xff000000)) != 0x06000000 || ilOffset < 0) return null;
        var handle = MetadataTokens.MethodDefinitionHandle(methodToken & 0x00ffffff).ToDebugInformationHandle();
        var debug = reader.GetMethodDebugInformation(handle);
        SequencePoint? best = null;
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.Offset > ilOffset) break;
            if (!point.IsHidden) best = point;
        }
        if (best is not { } match) return null;
        var document = match.Document.IsNil ? debug.Document : match.Document;
        return document.IsNil ? null : new SourceLocation(reader.GetString(reader.GetDocument(document).Name), match.StartLine);
    }

    private MetadataReader? GetReader(PdbInfo pdb, string? moduleName)
    {
        if (_readers.TryGetValue(pdb.Guid, out var existing)) return existing?.GetMetadataReader();
        var pdbName = Path.GetFileName(pdb.Path.Replace('\\', '/'));
        var candidates = new List<string> { pdb.Path };
        if (!string.IsNullOrWhiteSpace(moduleName)) candidates.Add(Path.ChangeExtension(moduleName, ".pdb"));
        candidates.AddRange(_directories.Select(directory => Path.Combine(directory, pdbName)));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(candidate));
                if (Matches(provider.GetMetadataReader(), pdb.Guid))
                {
                    _readers[pdb.Guid] = provider;
                    return provider.GetMetadataReader();
                }
                provider.Dispose();
            }
            catch { }
        }
        // Published managed assemblies can carry embedded portable symbols.
        var images = string.IsNullOrWhiteSpace(moduleName) ? [] : new[] { moduleName }
            .Concat(_directories.Select(directory => Path.Combine(directory, Path.GetFileName(moduleName.Replace('\\', '/')))));
        foreach (var image in images)
        {
            if (!File.Exists(image)) continue;
            try
            {
                using var file = File.OpenRead(image);
                using var pe = new PEReader(file);
                foreach (var entry in pe.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb) continue;
                    var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    if (Matches(provider.GetMetadataReader(), pdb.Guid))
                    {
                        _readers[pdb.Guid] = provider;
                        return provider.GetMetadataReader();
                    }
                    provider.Dispose();
                }
            }
            catch { }
        }
        _readers[pdb.Guid] = null;
        return null;
    }

    private static bool Matches(MetadataReader reader, Guid expected) =>
        reader.DebugMetadataHeader is { } header && new Guid(header.Id.AsSpan()[..16]) == expected;

    public void Dispose()
    {
        foreach (var reader in _readers.Values) reader?.Dispose();
        _readers.Clear();
    }
}

internal sealed record SourceLocation(string File, int Line);
