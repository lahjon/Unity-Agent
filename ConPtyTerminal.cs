using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityAgent.Models;

namespace UnityAgent
{
    public sealed class ConPtyTerminal : IDisposable
    {
        // ── P/Invoke ────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // ── Structs ─────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        // Constants
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
        private const uint STILL_ACTIVE = 259;
        private const int STARTF_USESTDHANDLES = 0x00000100;

        // ── Fields ──────────────────────────────────────────────────

        private IntPtr _pseudoConsole;
        private IntPtr _pipeInRead, _pipeInWrite;
        private IntPtr _pipeOutRead, _pipeOutWrite;
        private IntPtr _processHandle, _threadHandle;
        private IntPtr _attrList;
        private Thread? _readThread;
        private volatile bool _disposed;

        private readonly VtScreenBuffer _screenBuffer;

        // Per-terminal command history
        public List<string> CommandHistory { get; } = new();
        public int HistoryIndex { get; set; } = -1;

        /// <summary>True once the user has sent at least one command.</summary>
        public bool HasBeenUsed { get; set; }

        public bool HasExited
        {
            get
            {
                if (_disposed) return true;
                var handle = _processHandle;
                if (handle == IntPtr.Zero) return true;
                return WaitForSingleObject(handle, 0) != 258; // WAIT_TIMEOUT = 258
            }
        }

        // ── Events ──────────────────────────────────────────────────

        /// <summary>Fires when new output has been processed into the screen buffer.</summary>
        public event Action? OutputReceived;
        public event Action? Exited;

        // ── Constructor ─────────────────────────────────────────────

        public string WorkingDirectory { get; }

        public ConPtyTerminal(string workingDirectory, short cols = 120, short rows = 30)
        {
            WorkingDirectory = workingDirectory;
            _screenBuffer = new VtScreenBuffer(cols, rows);

            try
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    bInheritHandle = true
                };

                // Create pipes: input pipe (we write to pipeInWrite, console reads from pipeInRead)
                if (!CreatePipe(out _pipeInRead, out _pipeInWrite, ref sa, 0))
                    throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");

                // Output pipe (console writes to pipeOutWrite, we read from pipeOutRead)
                if (!CreatePipe(out _pipeOutRead, out _pipeOutWrite, ref sa, 0))
                    throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");

                // Create pseudo console
                var size = new COORD { X = cols, Y = rows };
                int hr = CreatePseudoConsole(size, _pipeInRead, _pipeOutWrite, 0, out _pseudoConsole);
                if (hr != 0)
                    throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

                // Initialize thread attribute list
                var listSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
                _attrList = Marshal.AllocHGlobal(listSize);
                if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize))
                    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

                if (!UpdateProcThreadAttribute(
                    _attrList, 0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _pseudoConsole, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

                // Resolve cmd.exe via %ComSpec% for reliability
                var cmdPath = Environment.GetEnvironmentVariable("ComSpec")
                              ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

                // Create process
                var si = new STARTUPINFOEX
                {
                    StartupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>()
                    },
                    lpAttributeList = _attrList
                };

                if (!CreateProcessW(
                    null, cmdPath,
                    IntPtr.Zero, IntPtr.Zero,
                    false, EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, workingDirectory,
                    ref si, out var pi))
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

                _processHandle = pi.hProcess;
                _threadHandle = pi.hThread;
                ProcessId = pi.dwProcessId;

                // Close the pipe ends the console owns
                CloseHandle(_pipeInRead);
                _pipeInRead = IntPtr.Zero;
                CloseHandle(_pipeOutWrite);
                _pipeOutWrite = IntPtr.Zero;

                // Start reading output
                _readThread = new Thread(ReadOutputLoop)
                {
                    IsBackground = true,
                    Name = $"ConPTY-Reader-{pi.dwProcessId}"
                };
                _readThread.Start();
            }
            catch
            {
                // Clean up any resources allocated before the failure
                CleanupNativeResources();
                throw;
            }
        }

        public int ProcessId { get; }

        // ── Output Reading ──────────────────────────────────────────

        private void ReadOutputLoop()
        {
            var buffer = new byte[4096];
            while (!_disposed)
            {
                bool ok = ReadFile(_pipeOutRead, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero);
                if (!ok || bytesRead == 0)
                    break;

                var raw = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                _screenBuffer.Process(raw);

                OutputReceived?.Invoke();
            }

            if (!_disposed)
                Exited?.Invoke();
        }

        // ── Public Methods ──────────────────────────────────────────

        public void SendLine(string text)
        {
            SendRaw(text + "\r\n");
        }

        public void SendRaw(string text)
        {
            if (_disposed) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteFile(_pipeInWrite, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
        }

        public void SendInterrupt()
        {
            SendRaw("\x03"); // Ctrl+C
        }

        public string GetOutputText()
        {
            return _screenBuffer.Render();
        }

        public void ClearOutput()
        {
            _screenBuffer.Clear();
        }

        // ── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Close pseudo console first - this will cause ReadFile to fail and exit the read thread
            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }

            // Wait for the read thread to finish so no more events fire after this point
            try { _readThread?.Join(2000); } catch { }

            CleanupNativeResources();
        }

        private void CleanupNativeResources()
        {
            // Terminate the process
            if (_processHandle != IntPtr.Zero)
            {
                try { TerminateProcess(_processHandle, 0); } catch { }
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }

            if (_threadHandle != IntPtr.Zero)
            {
                CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }

            // Close remaining pipe handles
            if (_pipeInWrite != IntPtr.Zero) { CloseHandle(_pipeInWrite); _pipeInWrite = IntPtr.Zero; }
            if (_pipeOutRead != IntPtr.Zero) { CloseHandle(_pipeOutRead); _pipeOutRead = IntPtr.Zero; }
            if (_pipeInRead != IntPtr.Zero) { CloseHandle(_pipeInRead); _pipeInRead = IntPtr.Zero; }
            if (_pipeOutWrite != IntPtr.Zero) { CloseHandle(_pipeOutWrite); _pipeOutWrite = IntPtr.Zero; }

            // Clean up attribute list
            if (_attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
                _attrList = IntPtr.Zero;
            }
        }
    }
}
