using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MolaGPT.CrashReporter;

internal static class NativeMiniDumpWriter
{
    public static (bool Success, string? Error) TryWrite(int processId, string outputPath, CrashCaptureRequest request)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Minidump capture requires Windows.");
        var success = false;
        try
        {
            using var process = OpenProcess(0x0400 | 0x0010, false, processId);
            if (process.IsInvalid) return (false, new Win32Exception(Marshal.GetLastWin32Error()).Message);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dbghelp.dll"));
            var exceptionBuffer = IntPtr.Zero;
            try
            {
                // Private RW memory contains the CLR's runtime structures needed by ClrMD.
                // This is not MiniDumpWithFullMemory, but can include application heap data.
                const uint dumpType = 0x00000001 | 0x00000020 | 0x00000040 | 0x00000200 | 0x00000800 | 0x00001000;
                if (request.ExceptionPointers != 0)
                {
                    var exceptionInfo = new MiniDumpExceptionInformation
                    {
                        ThreadId = request.ThreadId,
                        ExceptionPointers = (IntPtr)unchecked((long)request.ExceptionPointers),
                        ClientPointers = true
                    };
                    exceptionBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<MiniDumpExceptionInformation>());
                    Marshal.StructureToPtr(exceptionInfo, exceptionBuffer, false);
                }
                var writer = Marshal.GetDelegateForFunctionPointer<MiniDumpWriteDumpDelegate>(
                    NativeLibrary.GetExport(library, "MiniDumpWriteDump"));
                success = writer(process, (uint)processId, output.SafeFileHandle, dumpType,
                    exceptionBuffer, IntPtr.Zero, IntPtr.Zero);
                return success ? (true, null) : (false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            finally
            {
                if (exceptionBuffer != IntPtr.Zero) Marshal.FreeHGlobal(exceptionBuffer);
                NativeLibrary.Free(library);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (!success)
            {
                try { File.Delete(outputPath); }
                catch { }
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct MiniDumpExceptionInformation
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MiniDumpWriteDumpDelegate(SafeProcessHandle process, uint processId, SafeFileHandle file,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
