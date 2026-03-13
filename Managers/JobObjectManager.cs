using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spritely.Managers
{
    /// <summary>
    /// Wraps a Windows Job Object so every child process (Claude CLI, PowerShell, etc.)
    /// is automatically killed when Spritely exits — even on crash.
    /// Call <see cref="Instance"/>.<see cref="AssignProcess"/> right after Process.Start().
    /// </summary>
    public sealed class JobObjectManager : IDisposable
    {
        public static readonly JobObjectManager Instance = new();

        private readonly IntPtr _jobHandle;

        private JobObjectManager()
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, "SpritelyChildProcesses");
            if (_jobHandle == IntPtr.Zero)
            {
                AppLogger.Warn("JobObject", $"CreateJobObject failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            // Kill all assigned processes when the job handle is closed (i.e. when Spritely exits).
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var infoSize = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var infoPtr = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)infoSize))
                    AppLogger.Warn("JobObject", $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        /// <summary>
        /// Assigns a process to the job object. Call immediately after Process.Start().
        /// Safe to call even if the job object failed to initialize — it will just log and return.
        /// </summary>
        public void AssignProcess(Process process)
        {
            if (_jobHandle == IntPtr.Zero) return;

            try
            {
                if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                    AppLogger.Debug("JobObject", $"AssignProcessToJobObject failed for PID {process.Id}: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("JobObject", $"Failed to assign PID to job: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_jobHandle != IntPtr.Zero)
                CloseHandle(_jobHandle);
        }

        // ── P/Invoke ──────────────────────────────────────────────

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
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
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
