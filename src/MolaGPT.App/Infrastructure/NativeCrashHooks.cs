using System.Runtime.InteropServices;

namespace MolaGPT.App.Infrastructure;

internal static class NativeCrashHooks
{
    private static readonly ExceptionFilter Filter = OnNativeException;
    private static Action<IntPtr, uint, uint, ulong>? _capture;

    public static uint CurrentThreadId => GetCurrentThreadId();

    public static void Initialize(Action<IntPtr, uint, uint, ulong> capture)
    {
        if (!OperatingSystem.IsWindows()) return;
        _capture = capture;
        // Suppress the Windows Error Reporting dialog / WerFault hang; our out-of-process
        // watcher already captures the minidump and presents the crash window.
        SetErrorMode(SetErrorMode(0) | 0x0001 | 0x0002);
        SetUnhandledExceptionFilter(Filter);
    }

    private static int OnNativeException(IntPtr exceptionPointers)
    {
        try
        {
            // EXCEPTION_POINTERS points to EXCEPTION_RECORD then CONTEXT in the app.
            var record = Marshal.ReadIntPtr(exceptionPointers);
            var code = unchecked((uint)Marshal.ReadInt32(record));
            var address = Marshal.ReadIntPtr(record, IntPtr.Size == 8 ? 16 : 12);
            _capture?.Invoke(exceptionPointers, GetCurrentThreadId(), code, unchecked((ulong)address.ToInt64()));
            return 1; // EXCEPTION_EXECUTE_HANDLER: terminate immediately with the exception code.
        }
        catch
        {
            return 0; // EXCEPTION_CONTINUE_SEARCH
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ExceptionFilter(IntPtr exceptionPointers);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(ExceptionFilter filter);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
