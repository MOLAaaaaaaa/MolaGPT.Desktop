using System.Runtime.InteropServices;

namespace MolaGPT.CrashReporter;

// MolaGPT.Desktop.exe is a WinExe, so CLI commands run from a terminal would
// print nowhere. Attach to the parent console when there is one; redirected
// handles (the app reading stderr) are inherited and keep working regardless.
internal static class ConsoleAttachment
{
    public static void TryAttachParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AttachConsole(-1); }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
