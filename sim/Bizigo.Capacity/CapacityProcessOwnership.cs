using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bizigo.Capacity;

/// <summary>Unix process session or Windows job, established before any payload runs.</summary>
internal sealed class CapacityProcessOwnership : IDisposable
{
    private readonly int sessionId;
    private readonly SafeFileHandle? job;

    public CapacityProcessOwnership(Process supervisor)
    {
        sessionId = supervisor.Id;
        if (OperatingSystem.IsWindows())
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(job, supervisor.Handle))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error);
            }
        }
    }

    public static void EstablishSession()
    {
        if (!OperatingSystem.IsWindows() && setsid() < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Terminate()
    {
        if (job is not null)
        {
            if (!TerminateJobObject(job, 137)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else KillSession(sessionId);
    }

    public static void TerminateOwnSession()
    {
        if (!OperatingSystem.IsWindows()) KillSession(Environment.ProcessId);
    }

    private static void KillSession(int id)
    {
        // A negative pid addresses the private process group, including reparented
        // descendants. ESRCH means it is already gone; other failures are surfaced.
        if (kill(-id, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
