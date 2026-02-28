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
        private int _disposed; // 0 = active, 1 = disposed; use Interlocked/Volatile for thread safety

        private readonly VtScreenBuffer _screenBuffer;

        // Per-terminal command history (thread-safe)
        private readonly List<string> _commandHistory = new();
        private readonly object _historyLock = new();
        public int HistoryIndex { get; set; } = -1;

        /// <summary>True once the user has sent at least one command.</summary>
        public bool HasBeenUsed { get; set; }

        public bool HasExited
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return true;
                var handle = _processHandle;
                if (handle == null || handle.IsInvalid || handle.IsClosed) return true;
                return WaitForSingleObject(handle, 0) != 258; // WAIT_TIMEOUT = 258
            }
        }

        // ── Command History (thread-safe) ──────────────────────────

        public void AddCommand(string command)
        {
            lock (_historyLock) _commandHistory.Add(command);
        }

        public int CommandCount
        {
            get { lock (_historyLock) return _commandHistory.Count; }
        }

        public string? GetCommandFromEnd(int historyIndex)
        {
            lock (_historyLock)
            {
                if (_commandHistory.Count == 0 || historyIndex < 0 || historyIndex >= _commandHistory.Count)
                    return null;
                return _commandHistory[_commandHistory.Count - 1 - historyIndex];
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
            var buffer = new byte[Constants.AppConstants.TerminalBufferSize];
            var pipe = _pipeOutRead!;
            while (Volatile.Read(ref _disposed) == 0)
            {
                bool ok = ReadFile(pipe, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero);
                if (!ok || bytesRead == 0)
                    break;

                var raw = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                _screenBuffer.Process(raw);

                OutputReceived?.Invoke();
            }

            if (Volatile.Read(ref _disposed) == 0)
                Exited?.Invoke();
        }

        // ── Public Methods ──────────────────────────────────────────

        public void SendLine(string text)
        {
            SendRaw(text + "\r\n");
        }

        public void SendRaw(string text)
        {
            // Capture the handle reference first — even if Dispose() nulls the field and
            // sets _disposed between these lines, our local 'pipe' keeps the SafeHandle
            // alive (preventing the native handle from being released under us).
            var pipe = _pipeInWrite;
            if (pipe == null || pipe.IsClosed || Volatile.Read(ref _disposed) != 0) return;
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
            // Atomic test-and-set: only one thread enters the cleanup path
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // 1. Let the read thread exit gracefully via its Volatile.Read(_disposed) check.
            //    If it's blocked inside ReadFile, the Join will timeout after 2 s and
            //    subsequent steps (TerminateProcess / ClosePseudoConsole) will unblock it.
            try { _readThread?.Join(2000); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "Read thread join failed", ex); }

            // 2. Terminate the process before closing its handle
            try
            {
                if (_processHandle is { IsInvalid: false, IsClosed: false })
                    TerminateProcess(_processHandle, 0);
            }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "TerminateProcess failed", ex); }

            // 3. Now close the pseudo console (safe — read thread has either exited or
            //    will exit once this breaks its ReadFile call)
            try { _pseudoConsole?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "ClosePseudoConsole failed", ex); }

            // 4. Dispose remaining handles — each in its own try-catch so one failure
            //    doesn't skip subsequent cleanup
            try { _processHandle?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "Process handle dispose failed", ex); }

            try { _threadHandle?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "Thread handle dispose failed", ex); }

            try { _pipeInWrite?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "PipeInWrite dispose failed", ex); }

            try { _pipeOutRead?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "PipeOutRead dispose failed", ex); }

            try { _pipeInRead?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "PipeInRead dispose failed", ex); }

            try { _pipeOutWrite?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "PipeOutWrite dispose failed", ex); }

            try { _attrList?.Dispose(); }
            catch (Exception ex) { Managers.AppLogger.Debug("ConPtyTerminal", "AttrList dispose failed", ex); }
        }
    }
}
