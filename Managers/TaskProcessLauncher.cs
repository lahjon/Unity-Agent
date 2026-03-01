using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages subprocess creation, process lifecycle (start/stop/pause/resume),
    /// streaming JSON output parsing, and script cleanup.
    /// Accepts its dependencies via constructor injection.
    /// </summary>
    public class TaskProcessLauncher
    {
        private readonly string _scriptDir;
        private readonly ConcurrentDictionary<string, StreamingToolState> _streamingToolState = new();
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly OutputProcessor _outputProcessor;
        private readonly Dispatcher _dispatcher;

        public ConcurrentDictionary<string, StreamingToolState> StreamingToolState => _streamingToolState;

        /// <summary>Fires when a managed process is started for a task.</summary>
        public event Action<string>? ProcessStarted;

        /// <summary>Fires when a task's process is paused.</summary>
        public event Action<string>? ProcessPaused;

        /// <summary>Fires when a task's process is resumed.</summary>
        public event Action<string>? ProcessResumed;

        public TaskProcessLauncher(
            string scriptDir,
            FileLockManager fileLockManager,
            OutputTabManager outputTabManager,
            OutputProcessor outputProcessor,
            Dispatcher dispatcher)
        {
            _scriptDir = scriptDir;
            _fileLockManager = fileLockManager;
            _outputTabManager = outputTabManager;
            _outputProcessor = outputProcessor;
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Creates a Process with standard output/error/event wiring for stream-json parsing.
        /// The caller supplies only the Exited callback. Returns the wired (but not started) Process.
        /// </summary>
        public Process CreateManagedProcess(
            string ps1File,
            string taskId,
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks,
            Action<int> onExited)
        {
            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: false);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data).Trim();
                if (string.IsNullOrEmpty(line)) return;
                _dispatcher.BeginInvoke(() => ParseStreamJson(taskId, line, activeTasks, historyTasks));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data);
                if (!string.IsNullOrWhiteSpace(line))
                    _dispatcher.BeginInvoke(() => _outputProcessor.AppendOutput(taskId, $"[stderr] {line}\n", activeTasks, historyTasks));
            };

            process.Exited += (_, _) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Could not get exit code for task {taskId}", ex); }
                    onExited(exitCode);
                });
            };

            return process;
        }

        /// <summary>Starts a managed process, assigns it to the task, and begins async output reading.</summary>
        public void StartManagedProcess(AgentTask task, Process process)
        {
            process.Start();
            task.Process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            ProcessStarted?.Invoke(task.Id);
        }

        public static void KillProcess(AgentTask task)
        {
            try
            {
                if (task.Process is { HasExited: false })
                    task.Process.Kill(true);
            }
            catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill process for task {task.Id}", ex); }
        }

        public void RemoveStreamingState(string taskId) => _streamingToolState.TryRemove(taskId, out _);

        public void CleanupScripts(string taskId)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_scriptDir, $"*_{taskId}*"))
                    File.Delete(f);
            }
            catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to cleanup scripts for task {taskId}", ex); }
        }

        // ── Process Suspend / Resume (P/Invoke) ────────────────────

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        private static List<int> GetProcessTree(int rootPid)
        {
            var result = new List<int> { rootPid };
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                var parentToChildren = new Dictionary<uint, List<uint>>();

                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (!parentToChildren.ContainsKey(entry.th32ParentProcessID))
                            parentToChildren[entry.th32ParentProcessID] = new List<uint>();
                        parentToChildren[entry.th32ParentProcessID].Add(entry.th32ProcessID);
                    } while (Process32Next(snapshot, ref entry));
                }

                var queue = new Queue<uint>();
                queue.Enqueue((uint)rootPid);
                var visited = new HashSet<uint> { (uint)rootPid };
                while (queue.Count > 0)
                {
                    var pid = queue.Dequeue();
                    if (parentToChildren.TryGetValue(pid, out var children))
                    {
                        foreach (var child in children)
                        {
                            if (visited.Add(child))
                            {
                                result.Add((int)child);
                                queue.Enqueue(child);
                            }
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        private static void SuspendProcessTree(Process process)
        {
            foreach (var pid in GetProcessTree(process.Id))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    foreach (ProcessThread thread in p.Threads)
                    {
                        var handle = IntPtr.Zero;
                        try
                        {
                            handle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                            if (handle != IntPtr.Zero)
                                SuspendThread(handle);
                        }
                        finally
                        {
                            if (handle != IntPtr.Zero)
                                CloseHandle(handle);
                        }
                    }
                }
                catch (Win32Exception ex) { AppLogger.Warn("TaskExecution", $"Win32 error suspending PID {pid}: {ex.Message} (code {ex.NativeErrorCode})"); }
                catch (ArgumentException ex) { AppLogger.Debug("TaskExecution", $"Process {pid} no longer exists: {ex.Message}"); }
                catch (InvalidOperationException ex) { AppLogger.Debug("TaskExecution", $"Process {pid} has exited: {ex.Message}"); }
            }
        }

        internal static void ResumeProcessTree(Process process)
        {
            foreach (var pid in GetProcessTree(process.Id))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    foreach (ProcessThread thread in p.Threads)
                    {
                        var handle = IntPtr.Zero;
                        try
                        {
                            handle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                            if (handle != IntPtr.Zero)
                                ResumeThread(handle);
                        }
                        finally
                        {
                            if (handle != IntPtr.Zero)
                                CloseHandle(handle);
                        }
                    }
                }
                catch (Win32Exception ex) { AppLogger.Warn("TaskExecution", $"Win32 error resuming PID {pid}: {ex.Message} (code {ex.NativeErrorCode})"); }
                catch (ArgumentException ex) { AppLogger.Debug("TaskExecution", $"Process {pid} no longer exists: {ex.Message}"); }
                catch (InvalidOperationException ex) { AppLogger.Debug("TaskExecution", $"Process {pid} has exited: {ex.Message}"); }
            }
        }

        public void PauseTask(AgentTask task)
        {
            if (task.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Planning)) return;
            if (task.Process is not { HasExited: false }) return;

            SuspendProcessTree(task.Process);
            task.Status = AgentTaskStatus.Paused;

            // Pause overnight timers if active
            task.OvernightIterationTimer?.Stop();
            task.OvernightRetryTimer?.Stop();

            _outputTabManager.UpdateTabHeader(task);
            ProcessPaused?.Invoke(task.Id);
        }

        public void ResumeTask(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (task.Status is not (AgentTaskStatus.Paused or AgentTaskStatus.Queued)) return;
            if (task.Process is not { HasExited: false }) return;

            ResumeProcessTree(task.Process);
            task.Status = task.IsPlanningBeforeQueue ? AgentTaskStatus.Planning : AgentTaskStatus.Running;

            // Restart overnight timers if applicable
            if (task.IsOvernight)
            {
                task.OvernightIterationTimer?.Start();
                task.OvernightRetryTimer?.Start();
            }

            _outputTabManager.UpdateTabHeader(task);
            _outputProcessor.AppendOutput(task.Id, "\n[HappyEngine] Task resumed.\n", activeTasks, historyTasks);
            ProcessResumed?.Invoke(task.Id);
        }

        // ── Stream JSON Parsing ─────────────────────────────────────

        internal static string FormatToolAction(string toolName, JsonElement? input)
        {
            string? GetProp(string name) =>
                input?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            string? FileName(string propName = "file_path") =>
                GetProp(propName) is string p ? Path.GetFileName(p) : null;

            return toolName switch
            {
                "Read" => $"Reading {FileName() ?? "file"}",
                "Edit" => $"Editing {FileName() ?? "file"}",
                "Write" => $"Writing {FileName() ?? "file"}",
                "MultiEdit" => $"Editing {FileName() ?? "file"}",
                "NotebookEdit" => $"Editing notebook {FileName("notebook_path") ?? "file"}",
                "Grep" => GetProp("pattern") is string pat
                    ? $"Searching for \"{(pat.Length > 60 ? pat[..60] + "..." : pat)}\""
                    : "Searching code",
                "Glob" => GetProp("pattern") is string g
                    ? $"Finding files matching \"{g}\""
                    : "Finding files",
                "Bash" => GetProp("description") is string desc ? $"Running: {desc}"
                    : GetProp("command") is string cmd
                        ? $"Running: {(cmd.Length > 80 ? cmd[..80] + "..." : cmd)}"
                        : "Running command",
                "Task" => GetProp("description") is string d ? $"Starting agent: {d}" : "Starting agent",
                "WebFetch" => "Fetching web content",
                "WebSearch" => GetProp("query") is string q ? $"Searching web: {q}" : "Searching web",
                "TodoWrite" => "Updating task list",
                "AskUserQuestion" => "Waiting for input",
                "EnterPlanMode" => "Entering plan mode",
                "ExitPlanMode" => "Plan ready for review",
                "Skill" => GetProp("skill") is string s ? $"Running skill: {s}" : "Running skill",
                "TaskOutput" => "Checking agent output",
                "TaskStop" => "Stopping agent",
                "EnterWorktree" => "Creating worktree",
                _ => $"Using {toolName}"
            };
        }

        private void ParseStreamJson(string taskId, string line,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                switch (type)
                {
                    case "assistant":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                                if (blockType == "text" && block.TryGetProperty("text", out var text))
                                {
                                    _outputProcessor.AppendOutput(taskId, text.GetString() + "\n", activeTasks, historyTasks);
                                }
                                else if (blockType == "tool_use")
                                {
                                    var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "unknown";
                                    JsonElement? toolInput = block.TryGetProperty("input", out var inp) ? inp : null;
                                    var actionText = FormatToolAction(toolName ?? "unknown", toolInput);
                                    _outputProcessor.AppendOutput(taskId, $"\n{actionText}\n", activeTasks, historyTasks);
                                    var actionTask = activeTasks.FirstOrDefault(t => t.Id == taskId);
                                    actionTask?.AddToolActivity(actionText);

                                    if (TaskLauncher.IsFileModifyTool(toolName) && toolInput != null)
                                    {
                                        var fp = FileLockManager.ExtractFilePath(toolInput.Value);
                                        if (!string.IsNullOrEmpty(fp) && !_fileLockManager.TryAcquireOrConflict(taskId, fp, toolName!, activeTasks,
                                            (tid, txt) => _outputProcessor.AppendOutput(tid, txt, activeTasks, historyTasks)))
                                        {
                                            _outputTabManager.UpdateTabHeader(activeTasks.FirstOrDefault(t => t.Id == taskId)!);
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var cb))
                        {
                            var cbType = cb.TryGetProperty("type", out var cbt) ? cbt.GetString() : null;
                            if (cbType == "tool_use")
                            {
                                var toolName = cb.TryGetProperty("name", out var tn) ? tn.GetString() : "tool";
                                var actionText = FormatToolAction(toolName ?? "tool", null);
                                _outputProcessor.AppendOutput(taskId, $"\n{actionText}...\n", activeTasks, historyTasks);
                                var actionTask = activeTasks.FirstOrDefault(t => t.Id == taskId);
                                actionTask?.AddToolActivity(actionText);

                                _streamingToolState[taskId] = new StreamingToolState
                                {
                                    CurrentToolName = toolName ?? "tool",
                                    IsFileModifyTool = TaskLauncher.IsFileModifyTool(toolName),
                                    JsonAccumulator = new StringBuilder()
                                };
                            }
                            else if (cbType == "thinking")
                            {
                                _outputProcessor.AppendOutput(taskId, "[Thinking...]\n", activeTasks, historyTasks);
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (deltaType == "text_delta" && delta.TryGetProperty("text", out var deltaText))
                            {
                                _outputProcessor.AppendOutput(taskId, deltaText.GetString() ?? "", activeTasks, historyTasks);
                            }
                            else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
                            {
                                var t = thinking.GetString() ?? "";
                                if (t.Length > 0)
                                    _outputProcessor.AppendOutput(taskId, t, activeTasks, historyTasks);
                            }
                            else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var partialJson))
                            {
                                if (_streamingToolState.TryGetValue(taskId, out var state) && state.IsFileModifyTool && !state.FilePathChecked)
                                {
                                    state.JsonAccumulator.Append(partialJson.GetString() ?? "");
                                    var fp = FileLockManager.TryExtractFilePathFromPartial(state.JsonAccumulator.ToString());
                                    if (fp != null)
                                    {
                                        state.FilePathChecked = true;
                                        if (!_fileLockManager.TryAcquireOrConflict(taskId, fp, state.CurrentToolName!, activeTasks,
                                            (tid, txt) => _outputProcessor.AppendOutput(tid, txt, activeTasks, historyTasks)))
                                        {
                                            _outputTabManager.UpdateTabHeader(activeTasks.FirstOrDefault(t => t.Id == taskId)!);
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case "content_block_stop":
                        if (_streamingToolState.TryGetValue(taskId, out var stopState) && stopState.IsFileModifyTool && !stopState.FilePathChecked)
                        {
                            var accumulated = stopState.JsonAccumulator.ToString();
                            if (!string.IsNullOrEmpty(accumulated))
                            {
                                try
                                {
                                    using var inputDoc = JsonDocument.Parse("{" + accumulated + "}");
                                    var fp = FileLockManager.ExtractFilePath(inputDoc.RootElement);
                                    if (!string.IsNullOrEmpty(fp))
                                    {
                                        stopState.FilePathChecked = true;
                                        if (!_fileLockManager.TryAcquireOrConflict(taskId, fp, stopState.CurrentToolName!, activeTasks,
                                            (tid, txt) => _outputProcessor.AppendOutput(tid, txt, activeTasks, historyTasks)))
                                        {
                                            _streamingToolState.TryRemove(taskId, out _);
                                            _outputTabManager.UpdateTabHeader(activeTasks.FirstOrDefault(t => t.Id == taskId)!);
                                            return;
                                        }
                                    }
                                }
                                catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to parse streaming tool args for task {taskId}", ex); }
                            }
                        }
                        _streamingToolState.TryRemove(taskId, out _);
                        _outputProcessor.AppendOutput(taskId, "\n", activeTasks, historyTasks);
                        break;

                    case "result":
                        if (root.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.String)
                        {
                            _outputProcessor.AppendOutput(taskId, $"\n{result.GetString()}\n", activeTasks, historyTasks);
                        }
                        else if (root.TryGetProperty("subtype", out var subtype))
                        {
                            _outputProcessor.AppendOutput(taskId, $"\n[Result: {subtype.GetString()}]\n", activeTasks, historyTasks);
                        }
                        break;

                    case "system":
                        // Capture conversation/session ID for resume support
                        {
                            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
                            if (task != null)
                            {
                                if (root.TryGetProperty("session_id", out var sessionIdProp))
                                    task.ConversationId = sessionIdProp.GetString();
                                else if (root.TryGetProperty("conversation_id", out var convIdProp))
                                    task.ConversationId = convIdProp.GetString();
                            }
                        }
                        break;

                    case "error":
                        var errMsg = root.TryGetProperty("error", out var err)
                            ? (err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString())
                            : "Unknown error";
                        _outputProcessor.AppendOutput(taskId, $"\n[Error] {errMsg}\n", activeTasks, historyTasks);
                        break;

                    case "message_start":
                        if (root.TryGetProperty("message", out var startMsg) &&
                            startMsg.TryGetProperty("usage", out var startUsage))
                        {
                            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
                            if (task != null)
                            {
                                var inTok = startUsage.TryGetProperty("input_tokens", out var sit) ? sit.GetInt64() : 0;
                                var outTok = startUsage.TryGetProperty("output_tokens", out var sot) ? sot.GetInt64() : 0;
                                var cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var crt) ? crt.GetInt64() : 0;
                                var cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cct) ? cct.GetInt64() : 0;
                                task.AddTokenUsage(inTok, outTok, cacheRead, cacheCreate);
                            }
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("usage", out var deltaUsage))
                        {
                            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
                            if (task != null)
                            {
                                var outTok = deltaUsage.TryGetProperty("output_tokens", out var dot) ? dot.GetInt64() : 0;
                                if (outTok > 0)
                                    task.AddTokenUsage(0, outTok);
                            }
                        }
                        break;

                    default:
                        if (type != null && type != "ping" && type != "message_stop" && type != "user")
                            _outputProcessor.AppendOutput(taskId, $"[{type}]\n", activeTasks, historyTasks);
                        break;
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _outputProcessor.AppendOutput(taskId, line + "\n", activeTasks, historyTasks);
            }
        }
    }
}
