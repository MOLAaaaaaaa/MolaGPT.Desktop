using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;

namespace MolaGPT.CrashReporter;

// DbgHelp is single-threaded. Each CLI analysis owns exactly one session.
internal sealed class NativeSymbolSession : IDisposable
{
    private readonly IntPtr _library;
    private readonly IntPtr _session = new(0x4d4f4c41);
    private readonly IDataReader _reader;
    private readonly List<(ulong Base, ulong Size, string Name)> _modules = [];
    private readonly SymFromAddr _fromAddress;
    private readonly SymGetLine _getLine;
    private readonly SymCleanup _cleanup;
    private readonly StackWalk _walk;
    private readonly ReadMemory _readMemory;
    private readonly ModuleBase _moduleBase;
    private readonly IntPtr _functionTable;
    private bool _initialized;
    private bool _disposed;

    private NativeSymbolSession(IntPtr library, IDataReader reader)
    {
        _library = library;
        _reader = reader;
        _fromAddress = Export<SymFromAddr>("SymFromAddrW");
        _getLine = Export<SymGetLine>("SymGetLineFromAddrW64");
        _cleanup = Export<SymCleanup>("SymCleanup");
        _walk = Export<StackWalk>("StackWalk64");
        _functionTable = NativeLibrary.GetExport(library, "SymFunctionTableAccess64");
        _readMemory = ReadDumpMemory;
        _moduleBase = (_, address) => FindModule(address).Base;
    }

    public static NativeSymbolSession? TryCreate(DataTarget target, string symbolPath, IList<string> warnings)
    {
        if (!OperatingSystem.IsWindows() || target.DataReader.Architecture != Architecture.X64)
        {
            warnings.Add("Native stack unwinding requires Windows and an x64 dump.");
            return null;
        }
        var library = IntPtr.Zero;
        NativeSymbolSession? session = null;
        try
        {
            library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dbghelp.dll"));
            session = new NativeSymbolSession(library, target.DataReader);
            // UNDNAME | DEFERRED_LOADS | LOAD_LINES | EXACT_SYMBOLS | FAIL_CRITICAL_ERRORS.
            session.Export<SymSetOptions>("SymSetOptions")(0x2 | 0x4 | 0x10 | 0x400 | 0x200 | 0x80000);
            if (!session.Export<SymInitialize>("SymInitializeW")(session._session, symbolPath, false))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            session._initialized = true;
            var load = session.Export<SymLoadModule>("SymLoadModuleExW");
            foreach (var module in target.EnumerateModules())
            {
                if (module.ImageBase == 0 || module.ImageSize <= 0) continue;
                var name = Path.GetFileName(module.FileName.Replace('\\', '/'));
                session._modules.Add((module.ImageBase, (ulong)module.ImageSize, name));
                load(session._session, IntPtr.Zero, module.FileName, name, module.ImageBase,
                    (uint)Math.Min(module.ImageSize, uint.MaxValue), IntPtr.Zero, 0);
            }
            return session;
        }
        catch (Exception ex)
        {
            warnings.Add("Native symbol initialization failed: " + ex.Message);
            if (session is not null) session.Dispose();
            else if (library != IntPtr.Zero) NativeLibrary.Free(library);
            return null;
        }
    }

    public IEnumerable<uint> ThreadIds => _reader is IThreadReader threads ? threads.EnumerateOSThreadIds() : [];

    public IEnumerable<ulong> Walk(uint threadId)
    {
        var context = new byte[1232]; // Windows AMD64 CONTEXT, aligned by AllocHGlobal below.
        if (!_reader.GetThreadContext(threadId, 0x0010001f, context)) yield break;
        var frame = new StackFrame
        {
            Pc = new Address64(BinaryPrimitives.ReadUInt64LittleEndian(context.AsSpan(248))),
            Stack = new Address64(BinaryPrimitives.ReadUInt64LittleEndian(context.AsSpan(152))),
            Frame = new Address64(BinaryPrimitives.ReadUInt64LittleEndian(context.AsSpan(160)))
        };
        var nativeContext = Marshal.AllocHGlobal(context.Length + 16);
        var alignedContext = new IntPtr((nativeContext.ToInt64() + 15) & ~15L);
        try
        {
            Marshal.Copy(context, 0, alignedContext, context.Length);
            var visited = new HashSet<(ulong Pc, ulong Sp)>();
            if (frame.Pc.Offset != 0)
            {
                visited.Add((frame.Pc.Offset, frame.Stack.Offset));
                yield return frame.Pc.Offset;
            }
            for (var index = 0; index < 512; index++)
            {
                if (!_walk(0x8664, _session, (IntPtr)threadId, ref frame, alignedContext,
                    _readMemory, _functionTable, _moduleBase, IntPtr.Zero) || frame.Pc.Offset == 0)
                    break;
                if (!visited.Add((frame.Pc.Offset, frame.Stack.Offset)))
                {
                    if (index == 0) continue; // StackWalk64 can return the initial context first.
                    break;
                }
                yield return frame.Pc.Offset;
            }
        }
        finally { Marshal.FreeHGlobal(nativeContext); }
    }

    public NativeSymbol Resolve(ulong address)
    {
        const int maxName = 1024;
        var length = Marshal.SizeOf<SymbolInfo>() + maxName * sizeof(char);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, buffer, length);
            Marshal.WriteInt32(buffer, Marshal.SizeOf<SymbolInfo>());
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<SymbolInfo>(nameof(SymbolInfo.MaxNameLength)).ToInt32(), maxName);
            string? symbol = null;
            ulong displacement = 0;
            if (_fromAddress(_session, address, out displacement, buffer))
            {
                var info = Marshal.PtrToStructure<SymbolInfo>(buffer);
                symbol = Marshal.PtrToStringUni(IntPtr.Add(buffer, Marshal.OffsetOf<SymbolInfo>(nameof(SymbolInfo.Name)).ToInt32()),
                    (int)Math.Min(info.NameLength, maxName - 1));
            }
            var line = new ImageHlpLine { Size = (uint)Marshal.SizeOf<ImageHlpLine>() };
            var hasLine = _getLine(_session, address, out _, ref line);
            return new NativeSymbol(FindModule(address).Name, symbol, displacement,
                hasLine ? Marshal.PtrToStringUni(line.FileName) : null, hasLine ? (int)line.Line : null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private (ulong Base, ulong Size, string Name) FindModule(ulong address) =>
        _modules.FirstOrDefault(module => address >= module.Base && address - module.Base < module.Size);

    private bool ReadDumpMemory(IntPtr process, ulong address, IntPtr buffer, uint size, out uint bytesRead)
    {
        bytesRead = 0;
        if (size == 0 || size > 1024 * 1024) return false;
        try
        {
            var bytes = new byte[size];
            var count = _reader.Read(address, bytes);
            if (count <= 0) return false;
            Marshal.Copy(bytes, 0, buffer, count);
            bytesRead = (uint)count;
            return true;
        }
        catch { return false; } // Never let a managed exception cross the native callback boundary.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialized) _cleanup(_session);
        NativeLibrary.Free(_library);
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SymInitialize(IntPtr process, string path, [MarshalAs(UnmanagedType.Bool)] bool invade);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint SymSetOptions(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate ulong SymLoadModule(IntPtr process, IntPtr file, string image, string name, ulong address, uint size, IntPtr data, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SymGetLine(IntPtr process, ulong address, out uint displacement, ref ImageHlpLine line);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SymCleanup(IntPtr process);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReadMemory(IntPtr process, ulong address, IntPtr buffer, uint size, out uint bytesRead);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong ModuleBase(IntPtr process, ulong address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool StackWalk(uint machine, IntPtr process, IntPtr thread, ref StackFrame frame, IntPtr context,
        ReadMemory read, IntPtr functionTable, ModuleBase moduleBase, IntPtr translate);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Address64(ulong offset)
    {
        public ulong Offset = offset;
        public ushort Segment;
        public uint Mode = 3; // AddrModeFlat
    }
    [StructLayout(LayoutKind.Explicit, Size = 264)]
    internal struct StackFrame
    {
        [FieldOffset(0)] public Address64 Pc;
        [FieldOffset(32)] public Address64 Frame;
        [FieldOffset(48)] public Address64 Stack;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SymbolInfo
    {
        public uint Size, TypeIndex;
        public ulong Reserved1, Reserved2;
        public uint Index, SymbolSize;
        public ulong ModuleBase;
        public uint Flags;
        public ulong Value, Address;
        public uint Register, Scope, Tag, NameLength, MaxNameLength;
        public char Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ImageHlpLine
    {
        public uint Size;
        public IntPtr Key;
        public uint Line;
        public IntPtr FileName;
        public ulong Address;
    }
}

internal sealed record NativeSymbol(string? Module, string? Symbol, ulong Displacement, string? SourceFile, int? Line);
