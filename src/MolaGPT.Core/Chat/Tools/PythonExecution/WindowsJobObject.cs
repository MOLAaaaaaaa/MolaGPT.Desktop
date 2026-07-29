using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>
/// Puts a spawned process and everything it starts into a Windows job object, so
/// the whole tree can be capped and disposed of as one unit.
///
/// This is a robustness boundary, not a security one — Windows does not treat job
/// objects as a security boundary, and code inside one can still do anything the
/// user can. What it does buy:
///
/// <list type="bullet">
///   <item>A runaway allocation hits the job's memory cap and dies, instead of
///     taking the machine's memory with it.</item>
///   <item>A loop that keeps spawning processes hits the process cap.</item>
///   <item>Everything dies when the job handle closes — including grandchildren
///     that re-parented and would survive <c>Kill(entireProcessTree)</c>, and
///     including the case where MolaGPT itself is killed mid-run.</item>
///   <item>A crashing child dies quietly instead of raising a Windows error
///     dialog that blocks the run until someone clicks it.</item>
/// </list>
///
/// Creation is best-effort: if the OS refuses, callers run without it rather than
/// failing the tool call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObject : IDisposable
{
    private nint _handle;

    private WindowsJobObject(nint handle) => _handle = handle;

    /// <summary>
    /// Create a job with the given caps, or null if unavailable on this platform
    /// or refused by the OS.
    /// </summary>
    public static WindowsJobObject? TryCreate(long memoryLimitBytes, int activeProcessLimit, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = nint.Zero;
        try
        {
            handle = CreateJobObjectW(nint.Zero, null);
            if (handle == nint.Zero)
                throw new Win32Exception();

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                 | JOB_OBJECT_LIMIT_JOB_MEMORY
                                 | JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                                 | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                    ActiveProcessLimit = (uint)Math.Max(1, activeProcessLimit),
                },
                JobMemoryLimit = (nuint)Math.Max(1, memoryLimitBytes),
            };

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    throw new Win32Exception();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new WindowsJobObject(handle);
        }
        catch (Exception ex)
        {
            if (handle != nint.Zero) CloseHandle(handle);
            log?.Invoke("[python] 无法创建作业对象，本次不设资源上限：" + ex.Message);
            return null;
        }
    }

    /// <summary>Put a started process (and, by inheritance, its children) in the job.</summary>
    public bool TryAssign(Process process, Action<string>? log = null)
    {
        try
        {
            if (_handle == nint.Zero || process.HasExited) return false;
            if (AssignProcessToJobObject(_handle, process.Handle)) return true;
            throw new Win32Exception();
        }
        catch (Exception ex)
        {
            log?.Invoke("[python] 无法把进程加入作业对象：" + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        // Closing the last handle is what triggers KILL_ON_JOB_CLOSE, so this is
        // the teardown, not just a handle release.
        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero) CloseHandle(handle);
    }

    // ---- interop ----------------------------------------------------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x0000_0008;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x0000_0200;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x0000_0400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x0000_2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
