using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AgenticEngine.Models;
using Microsoft.Win32.SafeHandles;

namespace AgenticEngine
{
    public sealed class ConPtyTerminal : IDisposable
    {
        // ── SafeHandle wrappers ──────────────────────────────────────

        /// <summary>Kernel handle released via CloseHandle (pipes, process, thread).</summary>
        private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeKernelHandle() : base(true) { }

            public SafeKernelHandle(IntPtr existingHandle) : base(true)
            {
                SetHandle(existingHandle);
            }

            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        /// <summary>Pseudo-console handle released via ClosePseudoConsole.</summary>
        private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafePseudoConsoleHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                ClosePseudoConsole(handle);
                return true;
            }
        }

        /// <summary>Proc-thread attribute list released via DeleteProcThreadAttributeList + FreeHGlobal.</summary>
        private sealed class SafeAttributeListHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeAttributeListHandle(IntPtr buffer) : base(true)
            {
                SetHandle(buffer);
            }

            protected override bool ReleaseHandle()
            {
                DeleteProcThreadAttributeList(handle);
                Marshal.FreeHGlobal(handle);
                return true;
            }
        }

        // ── P/Invoke ────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeKernelHandle hInput, SafeKernelHandle hOutput, uint dwFlags, out SafePseudoConsoleHandle phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeKernelHandle hReadPipe, out SafeKernelHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeKernelHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeKernelHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

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
        private static extern uint WaitForSingleObject(SafeKernelHandle hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeKernelHandle hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeKernelHandle hProcess, out uint lpExitCode);

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

        private SafePseudoConsoleHandle? _pseudoConsole;
        private SafeKernelHandle? _pipeInRead, _pipeInWrite;
        private SafeKernelHandle? _pipeOutRead, _pipeOutWrite;
        private SafeKernelHandle? _processHandle, _threadHandle;
        private SafeAttributeListHandle? _attrList;
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
                if (handle == null || handle.IsInvalid || handle.IsClosed) return true;
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
                if (!CreatePipe(out _pipeInRead!, out _pipeInWrite!, ref sa, 0))
                    throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");

                // Output pipe (console writes to pipeOutWrite, we read from pipeOutRead)
                if (!CreatePipe(out _pipeOutRead!, out _pipeOutWrite!, ref sa, 0))
                    throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");

                // Create pseudo console
                var size = new COORD { X = cols, Y = rows };
                int hr = CreatePseudoConsole(size, _pipeInRead, _pipeOutWrite, 0, out _pseudoConsole!);
                if (hr != 0)
                    throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

                // Initialize thread attribute list
                var listSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
                _attrList = new SafeAttributeListHandle(Marshal.AllocHGlobal(listSize));
                if (!InitializeProcThreadAttributeList(_attrList.DangerousGetHandle(), 1, 0, ref listSize))
                    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

                if (!UpdateProcThreadAttribute(
                    _attrList.DangerousGetHandle(), 0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _pseudoConsole.DangerousGetHandle(), (IntPtr)IntPtr.Size,
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
                    lpAttributeList = _attrList.DangerousGetHandle()
                };

                if (!CreateProcessW(
                    null, cmdPath,
                    IntPtr.Zero, IntPtr.Zero,
                    false, EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, workingDirectory,
                    ref si, out var pi))
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

                // Prevent premature GC of SafeHandles whose raw values were embedded in the struct
                GC.KeepAlive(_attrList);
                GC.KeepAlive(_pseudoConsole);

                _processHandle = new SafeKernelHandle(pi.hProcess);
                _threadHandle = new SafeKernelHandle(pi.hThread);
                ProcessId = pi.dwProcessId;

                // Close the pipe ends the console owns
                _pipeInRead.Dispose();
                _pipeInRead = null;
                _pipeOutWrite.Dispose();
                _pipeOutWrite = null;

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
                // SafeHandles guarantee cleanup via finalizer, but dispose now for immediate release
                Dispose();
                throw;
            }
        }

        public int ProcessId { get; }

        // ── Output Reading ──────────────────────────────────────────

        private void ReadOutputLoop()
        {
            var buffer = new byte[4096];
            var pipe = _pipeOutRead!;
            while (!_disposed)
            {
                bool ok = ReadFile(pipe, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero);
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
            var pipe = _pipeInWrite;
            if (pipe == null || pipe.IsClosed) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteFile(pipe, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
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
            _pseudoConsole?.Dispose();
            _pseudoConsole = null;

            // Wait for the read thread to finish so no more events fire after this point
            try { _readThread?.Join(2000); } catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", $"Read thread join failed: {ex.Message}"); }

            // Terminate the process before closing its handle
            if (_processHandle is { IsInvalid: false, IsClosed: false })
            {
                try { TerminateProcess(_processHandle, 0); } catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", $"TerminateProcess failed: {ex.Message}"); }
            }

            _processHandle?.Dispose();
            _threadHandle?.Dispose();
            _pipeInWrite?.Dispose();
            _pipeOutRead?.Dispose();
            _pipeInRead?.Dispose();
            _pipeOutWrite?.Dispose();
            _attrList?.Dispose();
        }
    }
}
