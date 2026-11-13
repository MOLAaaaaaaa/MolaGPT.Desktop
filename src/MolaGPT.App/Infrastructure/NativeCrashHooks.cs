using System.Runtime.InteropServices;

namespace MolaGPT.App.Infrastructure;

internal static class NativeCrashHooks
{
    private static readonly ExceptionFilter Filter = OnNativeException;
    private static Action<IntPtr, uint, uint, ulong>? _capture;
    private static IntPtr _previousFilter;

    public static uint CurrentThreadId => GetCurrentThreadId();

    public static void Initialize(Action<IntPtr, uint, uint, ulong> capture)
    {
        if (!OperatingSystem.IsWindows()) return;
        _capture = capture;
        _previousFilter = SetUnhandledExceptionFilter(Filter);
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
        }
        catch
        {
            // Native failure handling is best effort; continue the OS/runtime termination path.
        }
        if (_previousFilter != IntPtr.Zero)
        {
            try { return Marshal.GetDelegateForFunctionPointer<ExceptionFilter>(_previousFilter)(exceptionPointers); }
            catch { }
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ExceptionFilter(IntPtr exceptionPointers);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(ExceptionFilter filter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
