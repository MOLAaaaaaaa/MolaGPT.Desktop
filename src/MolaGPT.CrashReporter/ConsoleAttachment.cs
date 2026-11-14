using System.Runtime.InteropServices;

namespace MolaGPT.CrashReporter;

// MolaGPT.Desktop.exe is a WinExe, so CLI commands run interactively from a
// terminal start with null std handles. Attach to the parent console only when
// neither stdout nor stderr is already redirected to a pipe or file.
internal static class ConsoleAttachment
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    public static void TryAttachParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (GetStdHandle(StdOutputHandle) == IntPtr.Zero && GetStdHandle(StdErrorHandle) == IntPtr.Zero)
                AttachConsole(-1);
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);
}
