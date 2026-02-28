using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgenticEngine.Managers
{
    public class TaskExecutionManager
    {
        private readonly string _scriptDir;
        private readonly ConcurrentDictionary<string, StreamingToolState> _streamingToolState = new();
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<string> _getSystemPrompt;
        private readonly Func<AgentTask, string> _getProjectDescription;
        private readonly Func<string, string> _getProjectRulesBlock;
        private readonly Func<string, bool> _isGameProject;
        private readonly MessageBusManager _messageBusManager;
        private readonly Dispatcher _dispatcher;

        private const int OvernightMaxRuntimeHours = 12;
        private const int OvernightIterationTimeoutMinutes = 30;
        private const int OvernightMaxConsecutiveFailures = 3;
        private const int OvernightOutputCapChars = 100_000;

        public ConcurrentDictionary<string, StreamingToolState> StreamingToolState => _streamingToolState;

        /// <summary>Fires when a task's process exits (with the task ID). Used to resume dependency-queued tasks.</summary>
        public event Action<string>? TaskCompleted;

        public TaskExecutionManager(
            string scriptDir,
            FileLockManager fileLockManager,
            OutputTabManager outputTabManager,
            Func<string> getSystemPrompt,
            Func<AgentTask, string> getProjectDescription,
            Func<string, string> getProjectRulesBlock,
            Func<string, bool> isGameProject,
            MessageBusManager messageBusManager,
            Dispatcher dispatcher)
        {
            _scriptDir = scriptDir;
            _fileLockManager = fileLockManager;
            _outputTabManager = outputTabManager;
            _getSystemPrompt = getSystemPrompt;
            _getProjectDescription = getProjectDescription;
            _getProjectRulesBlock = getProjectRulesBlock;
            _isGameProject = isGameProject;
            _messageBusManager = messageBusManager;
            _dispatcher = dispatcher;
        }

        public async System.Threading.Tasks.Task StartProcess(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            task.GitStartHash = await TaskLauncher.CaptureGitHeadAsync(task.ProjectPath, task.Cts.Token);

            if (task.IsOvernight)
                TaskLauncher.PrepareTaskForOvernightStart(task);

            var fullPrompt = TaskLauncher.BuildFullPrompt(_getSystemPrompt(), task, _getProjectDescription(task), _getProjectRulesBlock(task.ProjectPath), _isGameProject(task.ProjectPath));
            var projectPath = task.ProjectPath;

            if (task.UseMessageBus)
            {
                _messageBusManager.JoinBus(projectPath, task.Id, task.Summary ?? task.Description);
                var siblings = _messageBusManager.GetParticipants(projectPath)
                    .Where(p => p.TaskId != task.Id)
                    .Select(p => (p.TaskId, p.Summary))
                    .ToList();
                fullPrompt = TaskLauncher.BuildMessageBusBlock(task.Id, siblings) + fullPrompt;
            }

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            var claudeCmd = TaskLauncher.BuildClaudeCommand(task.SkipPermissions, task.RemoteSession, task.SpawnTeam);

            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(projectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            AppendOutput(task.Id, $"[AgenticEngine] Task #{task.TaskNumber} starting...\n", activeTasks, historyTasks);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                AppendOutput(task.Id, $"[AgenticEngine] Summary: {task.Summary}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[AgenticEngine] Project: {projectPath}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[AgenticEngine] Skip permissions: {task.SkipPermissions}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[AgenticEngine] Remote session: {task.RemoteSession}\n", activeTasks, historyTasks);
            if (task.UseMessageBus)
                AppendOutput(task.Id, $"[AgenticEngine] Message Bus: ON\n", activeTasks, historyTasks);
            if (task.ExtendedPlanning)
                AppendOutput(task.Id, $"[AgenticEngine] Extended planning: ON\n", activeTasks, historyTasks);
            if (task.IsOvernight)
            {
                AppendOutput(task.Id, $"[AgenticEngine] Overnight mode: ON (max {task.MaxIterations} iterations, 12h cap)\n", activeTasks, historyTasks);
                AppendOutput(task.Id, $"[AgenticEngine] Safety: skip-permissions forced, git blocked, 30min iteration timeout\n", activeTasks, historyTasks);
            }
            AppendOutput(task.Id, $"[AgenticEngine] Connecting to Claude...\n\n", activeTasks, historyTasks);

            var process = CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                CleanupScripts(task.Id);

                // Plan-before-queue: killed process needs to restart in plan mode
                if (task.NeedsPlanRestart)
                {
                    task.NeedsPlanRestart = false;
                    task.IsPlanningBeforeQueue = true;
                    task.Status = AgentTaskStatus.Planning;
                    task.StartTime = DateTime.Now;
                    AppendOutput(task.Id, "\n[AgenticEngine] Restarting in plan mode...\n\n", activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = StartProcess(task, activeTasks, historyTasks, moveToHistory);
                    return;
                }

                // Plan-before-queue: planning phase complete
                if (task.IsPlanningBeforeQueue)
                {
                    HandlePlanBeforeQueueCompletion(task, activeTasks, historyTasks, moveToHistory);
                    return;
                }

                // Already queued or cancelled — skip normal completion
                if (task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Cancelled)
                {
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                if (task.IsOvernight && task.Status == AgentTaskStatus.Running)
                {
                    // Don't release locks between overnight iterations — the task
                    // will continue working on the same files.  Locks are released
                    // when the overnight task finishes via FinishOvernightTask → moveToHistory.
                    HandleOvernightIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
                }
                else
                {
                    _fileLockManager.ReleaseTaskLocks(task.Id);
                    if (task.UseMessageBus)
                        _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
                    task.Status = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    AppendCompletionSummary(task, activeTasks, historyTasks);
                    var statusColor = exitCode == 0
                        ? (Brush)Application.Current.FindResource("Success")
                        : (Brush)Application.Current.FindResource("DangerBright");
                    AppendColoredOutput(task.Id,
                        $"\n[AgenticEngine] Process finished (exit code: {exitCode}).\n",
                        statusColor, activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }

                _fileLockManager.CheckQueuedTasks(activeTasks);
                TaskCompleted?.Invoke(task.Id);
            });

            try
            {
                StartManagedProcess(task, process);

                if (task.IsOvernight)
                {
                    var iterationTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(OvernightIterationTimeoutMinutes)
                    };
                    task.OvernightIterationTimer = iterationTimer;
                    iterationTimer.Tick += (_, _) =>
                    {
                        iterationTimer.Stop();
                        task.OvernightIterationTimer = null;
                        if (task.Process is { HasExited: false })
                        {
                            AppendOutput(task.Id, $"\n[Overnight] Iteration timeout ({OvernightIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                            try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck process for task {task.Id}", ex); }
                        }
                    };
                    iterationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[AgenticEngine] ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        public void LaunchHeadless(AgentTask task)
        {
            var fullPrompt = TaskLauncher.BuildFullPrompt(_getSystemPrompt(), task, _getProjectDescription(task), _getProjectRulesBlock(task.ProjectPath), _isGameProject(task.ProjectPath));
            var projectPath = task.ProjectPath;

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"headless_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildHeadlessPowerShellScript(projectPath, promptFile, task.SkipPermissions, task.RemoteSession, task.SpawnTeam),
                Encoding.UTF8);

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: true);
            psi.WorkingDirectory = projectPath;
            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Dialogs.DarkDialog.ShowConfirm($"Failed to launch terminal:\n{ex.Message}", "Launch Error");
            }
        }

        public void SendInput(AgentTask task, TextBox inputBox,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var text = inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputBox.Clear();
            SendFollowUp(task, text, activeTasks, historyTasks);
        }

        public void SendFollowUp(AgentTask task, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning && task.Process is { HasExited: false })
            {
                try
                {
                    AppendOutput(task.Id, $"\n> {text}\n", activeTasks, historyTasks);
                    task.Process.StandardInput.WriteLine(text);
                    return;
                }
                catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to write to stdin for task {task.Id}, starting follow-up", ex); }
            }

            task.Status = AgentTaskStatus.Running;
            task.EndTime = null;
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            _outputTabManager.UpdateTabHeader(task);

            // Use --resume with session ID when available, fall back to --continue
            var hasSessionId = !string.IsNullOrEmpty(task.ConversationId);
            var resumeFlag = hasSessionId
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var resumeLabel = hasSessionId
                ? $"--resume {task.ConversationId}"
                : "--continue";

            AppendOutput(task.Id, $"\n> {text}\n[AgenticEngine] Sending follow-up with {resumeLabel}...\n\n", activeTasks, historyTasks);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var followUpFile = Path.Combine(_scriptDir, $"followup_{task.Id}_{DateTime.Now.Ticks}.txt");
            File.WriteAllText(followUpFile, text, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"followup_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{followUpFile}'\n" +
                $"claude -p{skipFlag}{resumeFlag} --verbose --output-format stream-json $prompt\n",
                Encoding.UTF8);

            var process = CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                task.Status = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                AppendCompletionSummary(task, activeTasks, historyTasks);
                AppendOutput(task.Id, "\n[AgenticEngine] Follow-up complete.\n", activeTasks, historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            });

            try
            {
                StartManagedProcess(task, process);
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[AgenticEngine] Follow-up error: {ex.Message}\n", activeTasks, historyTasks);
            }
        }

        public void CancelTaskImmediate(AgentTask task)
        {
            if (task.IsFinished) return;

            // Resume suspended threads before killing so the process can exit cleanly
            if (task.Status == AgentTaskStatus.Paused && task.Process is { HasExited: false })
            {
                try { ResumeProcessTree(task.Process); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to resume process tree before cancel for task {task.Id}", ex); }
            }

            // Cancel cooperative async operations before killing the process
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }

            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            KillProcess(task);
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _streamingToolState.TryRemove(task.Id, out _);
            task.Cts?.Dispose();
            task.Cts = null;
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

        /// <summary>
        /// Creates a Process with standard output/error/event wiring for stream-json parsing.
        /// The caller supplies only the Exited callback. Returns the wired (but not started) Process.
        /// </summary>
        private Process CreateManagedProcess(
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
                    _dispatcher.BeginInvoke(() => AppendOutput(taskId, $"[stderr] {line}\n", activeTasks, historyTasks));
            };

            process.Exited += (_, _) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Could not get exit code for task {taskId}: {ex.Message}"); }
                    onExited(exitCode);
                });
            };

            return process;
        }

        /// <summary>Starts a managed process, assigns it to the task, and begins async output reading.</summary>
        private void StartManagedProcess(AgentTask task, Process process)
        {
            process.Start();
            task.Process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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
                        var handle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                        if (handle != IntPtr.Zero)
                        {
                            SuspendThread(handle);
                            CloseHandle(handle);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to suspend PID {pid}: {ex.Message}"); }
            }
        }

        private static void ResumeProcessTree(Process process)
        {
            foreach (var pid in GetProcessTree(process.Id))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    foreach (ProcessThread thread in p.Threads)
                    {
                        var handle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                        if (handle != IntPtr.Zero)
                        {
                            ResumeThread(handle);
                            CloseHandle(handle);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to resume PID {pid}: {ex.Message}"); }
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
            AppendOutput(task.Id, "\n[AgenticEngine] Task resumed.\n", activeTasks, historyTasks);
        }

        private static string FormatToolAction(string toolName, JsonElement? input)
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
                                    AppendOutput(taskId, text.GetString() + "\n", activeTasks, historyTasks);
                                }
                                else if (blockType == "tool_use")
                                {
                                    var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "unknown";
                                    JsonElement? toolInput = block.TryGetProperty("input", out var inp) ? inp : null;
                                    var actionText = FormatToolAction(toolName ?? "unknown", toolInput);
                                    AppendOutput(taskId, $"\n{actionText}\n", activeTasks, historyTasks);
                                    var actionTask = activeTasks.FirstOrDefault(t => t.Id == taskId);
                                    actionTask?.AddToolActivity(actionText);

                                    if (TaskLauncher.IsFileModifyTool(toolName) && toolInput != null)
                                    {
                                        var fp = FileLockManager.ExtractFilePath(toolInput.Value);
                                        if (!string.IsNullOrEmpty(fp) && !_fileLockManager.TryAcquireOrConflict(taskId, fp, toolName!, activeTasks,
                                            (tid, txt) => AppendOutput(tid, txt, activeTasks, historyTasks)))
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
                                AppendOutput(taskId, $"\n{actionText}...\n", activeTasks, historyTasks);
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
                                AppendOutput(taskId, "[Thinking...]\n", activeTasks, historyTasks);
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (deltaType == "text_delta" && delta.TryGetProperty("text", out var deltaText))
                            {
                                AppendOutput(taskId, deltaText.GetString() ?? "", activeTasks, historyTasks);
                            }
                            else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
                            {
                                var t = thinking.GetString() ?? "";
                                if (t.Length > 0)
                                    AppendOutput(taskId, t, activeTasks, historyTasks);
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
                                            (tid, txt) => AppendOutput(tid, txt, activeTasks, historyTasks)))
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
                                            (tid, txt) => AppendOutput(tid, txt, activeTasks, historyTasks)))
                                        {
                                            _streamingToolState.TryRemove(taskId, out _);
                                            _outputTabManager.UpdateTabHeader(activeTasks.FirstOrDefault(t => t.Id == taskId)!);
                                            return;
                                        }
                                    }
                                }
                                catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to parse streaming tool args for task {taskId}: {ex.Message}"); }
                            }
                        }
                        _streamingToolState.TryRemove(taskId, out _);
                        AppendOutput(taskId, "\n", activeTasks, historyTasks);
                        break;

                    case "result":
                        if (root.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.String)
                        {
                            AppendOutput(taskId, $"\n{result.GetString()}\n", activeTasks, historyTasks);
                        }
                        else if (root.TryGetProperty("subtype", out var subtype))
                        {
                            AppendOutput(taskId, $"\n[Result: {subtype.GetString()}]\n", activeTasks, historyTasks);
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
                        AppendOutput(taskId, $"\n[Error] {errMsg}\n", activeTasks, historyTasks);
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
                            AppendOutput(taskId, $"[{type}]\n", activeTasks, historyTasks);
                        break;
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(line))
                    AppendOutput(taskId, line + "\n", activeTasks, historyTasks);
            }
        }

        private void HandleOvernightIteration(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }

            if (task.Status != AgentTaskStatus.Running) return;

            var fullOutput = task.OutputBuilder.ToString();
            var iterationOutput = task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;

            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= OvernightMaxRuntimeHours)
            {
                AppendOutput(task.Id, $"\n[Overnight] Total runtime cap ({OvernightMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (TaskLauncher.CheckOvernightComplete(iterationOutput))
            {
                AppendOutput(task.Id, $"\n[Overnight] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Task finished.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (task.CurrentIteration >= task.MaxIterations)
            {
                AppendOutput(task.Id, $"\n[Overnight] Max iterations ({task.MaxIterations}) reached. Stopping.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveFailures++;
                AppendOutput(task.Id, $"\n[Overnight] Iteration exited with code {exitCode} (failure {task.ConsecutiveFailures}/{OvernightMaxConsecutiveFailures})\n", activeTasks, historyTasks);
                if (task.ConsecutiveFailures >= OvernightMaxConsecutiveFailures)
                {
                    AppendOutput(task.Id, $"\n[Overnight] {OvernightMaxConsecutiveFailures} consecutive failures detected (crash loop). Stopping.\n", activeTasks, historyTasks);
                    FinishOvernightTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
            }
            else
            {
                task.ConsecutiveFailures = 0;
            }

            if (TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                AppendOutput(task.Id, "\n[Overnight] Token limit hit. Retrying in 30 minutes...\n", activeTasks, historyTasks);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
                task.OvernightRetryTimer = timer;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    task.OvernightRetryTimer = null;
                    if (task.Status != AgentTaskStatus.Running) return;
                    if ((DateTime.Now - task.StartTime).TotalHours >= OvernightMaxRuntimeHours)
                    {
                        AppendOutput(task.Id, $"\n[Overnight] Runtime cap reached during retry wait. Stopping.\n", activeTasks, historyTasks);
                        FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                        return;
                    }
                    AppendOutput(task.Id, "[Overnight] Retrying...\n", activeTasks, historyTasks);
                    StartOvernightContinuation(task, activeTasks, historyTasks, moveToHistory);
                };
                timer.Start();
                return;
            }

            if (task.OutputBuilder.Length > OvernightOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - OvernightOutputCapChars, OvernightOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = 0;
            }

            task.CurrentIteration++;
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            AppendOutput(task.Id, $"\n[Overnight] Starting iteration {task.CurrentIteration}/{task.MaxIterations}...\n\n", activeTasks, historyTasks);
            StartOvernightContinuation(task, activeTasks, historyTasks, moveToHistory);
        }

        private void FinishOvernightTask(AgentTask task, AgentTaskStatus status,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            task.Status = status;
            task.EndTime = DateTime.Now;
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            var duration = task.EndTime.Value - task.StartTime;
            AppendOutput(task.Id, $"[Overnight] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).\n", activeTasks, historyTasks);
            AppendCompletionSummary(task, activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            moveToHistory(task);
        }

        private void StartOvernightContinuation(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var continuationPrompt = TaskLauncher.BuildOvernightContinuationPrompt(task.CurrentIteration, task.MaxIterations);

            var promptFile = Path.Combine(_scriptDir, $"overnight_{task.Id}_{task.CurrentIteration}.txt");
            File.WriteAllText(promptFile, continuationPrompt, Encoding.UTF8);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var teamFlag = task.SpawnTeam ? " --spawn-team" : "";
            var resumeFlag = !string.IsNullOrEmpty(task.ConversationId)
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose{resumeFlag} --output-format stream-json $prompt";

            var ps1File = Path.Combine(_scriptDir, $"overnight_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var process = CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                CleanupScripts(task.Id);
                HandleOvernightIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
            });

            try
            {
                StartManagedProcess(task, process);

                var iterationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(OvernightIterationTimeoutMinutes)
                };
                task.OvernightIterationTimer = iterationTimer;
                iterationTimer.Tick += (_, _) =>
                {
                    iterationTimer.Stop();
                    task.OvernightIterationTimer = null;
                    if (task.Process is { HasExited: false })
                    {
                        AppendOutput(task.Id, $"\n[Overnight] Iteration timeout ({OvernightIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                        try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck overnight process for task {task.Id}", ex); }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[Overnight] ERROR starting continuation: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        private void HandlePlanBeforeQueueCompletion(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.IsPlanningBeforeQueue = false;
            task.PlanOnly = false;

            // Extract execution prompt from plan output
            var output = task.OutputBuilder.ToString();
            var executionPrompt = TaskLauncher.ExtractExecutionPrompt(output);
            if (!string.IsNullOrEmpty(executionPrompt))
                task.StoredPrompt = executionPrompt;

            // Check dependency-based queue
            if (task.DependencyTaskIds.Count > 0)
            {
                var allResolved = task.DependencyTaskIds.All(depId =>
                {
                    var dep = activeTasks.FirstOrDefault(t => t.Id == depId);
                    return dep == null || dep.IsFinished;
                });

                if (!allResolved)
                {
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Plan complete, waiting for dependencies";
                    var blocker = activeTasks.FirstOrDefault(t =>
                        task.DependencyTaskIds.Contains(t.Id) && !t.IsFinished);
                    task.BlockedByTaskId = blocker?.Id;
                    task.BlockedByTaskNumber = blocker?.TaskNumber;
                    AppendOutput(task.Id,
                        $"\n[AgenticEngine] Planning complete. Queued — waiting for dependencies: " +
                        $"{string.Join(", ", task.DependencyTaskNumbers.Select(n => $"#{n}"))}\n",
                        activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    return;
                }
                // Dependencies resolved during planning — gather context before clearing
                task.DependencyContext = TaskLauncher.BuildDependencyContext(
                    task.DependencyTaskIds, activeTasks, historyTasks);
                task.DependencyTaskIds.Clear();
                task.DependencyTaskNumbers.Clear();
            }

            // Check file-lock-based queue
            if (!string.IsNullOrEmpty(task.PendingFileLockPath))
            {
                var filePath = task.PendingFileLockPath;
                var blockerId = task.PendingFileLockBlocker!;
                task.PendingFileLockPath = null;
                task.PendingFileLockBlocker = null;

                if (_fileLockManager.IsFileLocked(filePath))
                {
                    var blockerTask = activeTasks.FirstOrDefault(t => t.Id == blockerId);
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = $"Plan complete, file locked by #{blockerTask?.TaskNumber}";
                    task.BlockedByTaskId = blockerId;
                    task.BlockedByTaskNumber = blockerTask?.TaskNumber;
                    _fileLockManager.AddQueuedTaskInfo(task.Id, new QueuedTaskInfo
                    {
                        Task = task,
                        ConflictingFilePath = filePath,
                        BlockingTaskId = blockerId,
                        BlockedByTaskIds = new HashSet<string> { blockerId }
                    });
                    AppendOutput(task.Id, "\n[AgenticEngine] Planning complete. Queued — waiting for file lock to clear.\n", activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    return;
                }
                // File lock cleared during planning
            }

            // No more blockers — start execution
            task.Status = AgentTaskStatus.Running;
            task.StartTime = DateTime.Now;
            AppendOutput(task.Id, "\n[AgenticEngine] Planning complete. Starting execution...\n\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = StartProcess(task, activeTasks, historyTasks, moveToHistory);
        }

        private void AppendOutput(string taskId, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _outputTabManager.AppendOutput(taskId, text, activeTasks, historyTasks);
        }

        private void AppendColoredOutput(string taskId, string text, Brush foreground,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _outputTabManager.AppendColoredOutput(taskId, text, foreground, activeTasks, historyTasks);
        }

        private async void AppendCompletionSummary(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            // Detect recommendations from the current iteration output only (avoids re-detecting old recommendations)
            var fullOutput = task.OutputBuilder.ToString();
            var outputText = task.LastIterationOutputStart > 0 && task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;
            var recommendations = TaskLauncher.ExtractRecommendations(outputText);
            task.Recommendations = recommendations ?? "";

            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;
            var duration = (task.EndTime ?? DateTime.Now) - task.StartTime;
            try
            {
                var summary = await TaskLauncher.GenerateCompletionSummaryAsync(
                    task.ProjectPath, task.GitStartHash, task.Status, duration, ct);
                task.CompletionSummary = summary;
                AppendOutput(task.Id, summary, activeTasks, historyTasks);
            }
            catch (OperationCanceledException)
            {
                var summary = TaskLauncher.FormatCompletionSummary(task.Status, duration, null);
                task.CompletionSummary = summary;
                AppendOutput(task.Id, summary, activeTasks, historyTasks);
            }
        }

        private void CleanupScripts(string taskId)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_scriptDir, $"*_{taskId}*"))
                    File.Delete(f);
            }
            catch (Exception ex) { AppLogger.Debug("TaskExecution", $"Failed to cleanup scripts for task {taskId}: {ex.Message}"); }
        }

        // ── Overnight iteration decision logic (extracted for testability) ──

        internal enum OvernightAction { Skip, Finish, RetryAfterDelay, Continue }

        internal struct OvernightDecision
        {
            public OvernightAction Action;
            public AgentTaskStatus FinishStatus;
            public int ConsecutiveFailures;
            public bool TrimOutput;
        }

        /// <summary>
        /// Pure decision function that evaluates what the overnight loop should do next.
        /// Mirrors the logic in HandleOvernightIteration without side effects.
        /// </summary>
        internal static OvernightDecision EvaluateOvernightIteration(
            AgentTaskStatus currentStatus,
            TimeSpan totalRuntime,
            string iterationOutput,
            int currentIteration,
            int maxIterations,
            int exitCode,
            int consecutiveFailures,
            int outputLength)
        {
            if (currentStatus != AgentTaskStatus.Running)
                return new OvernightDecision { Action = OvernightAction.Skip };

            if (totalRuntime.TotalHours >= OvernightMaxRuntimeHours)
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (TaskLauncher.CheckOvernightComplete(iterationOutput))
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (currentIteration >= maxIterations)
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            var newFailures = consecutiveFailures;
            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                newFailures++;
                if (newFailures >= OvernightMaxConsecutiveFailures)
                    return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Failed, ConsecutiveFailures = newFailures };
            }
            else
            {
                newFailures = 0;
            }

            if (TaskLauncher.IsTokenLimitError(iterationOutput))
                return new OvernightDecision { Action = OvernightAction.RetryAfterDelay, ConsecutiveFailures = newFailures };

            return new OvernightDecision
            {
                Action = OvernightAction.Continue,
                ConsecutiveFailures = newFailures,
                TrimOutput = outputLength > OvernightOutputCapChars
            };
        }
    }
}
