using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UnityAgent.Managers
{
    public class TaskExecutionManager
    {
        private readonly string _scriptDir;
        private readonly System.Collections.Generic.Dictionary<string, StreamingToolState> _streamingToolState = new();
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<string> _getSystemPrompt;
        private readonly Func<AgentTask, string> _getProjectDescription;
        private readonly Dispatcher _dispatcher;

        private const int OvernightMaxRuntimeHours = 12;
        private const int OvernightIterationTimeoutMinutes = 30;
        private const int OvernightMaxConsecutiveFailures = 3;
        private const int OvernightOutputCapChars = 100_000;

        public System.Collections.Generic.Dictionary<string, StreamingToolState> StreamingToolState => _streamingToolState;

        /// <summary>Fires when a task's process exits (with the task ID). Used to resume dependency-queued tasks.</summary>
        public event Action<string>? TaskCompleted;

        public TaskExecutionManager(
            string scriptDir,
            FileLockManager fileLockManager,
            OutputTabManager outputTabManager,
            Func<string> getSystemPrompt,
            Func<AgentTask, string> getProjectDescription,
            Dispatcher dispatcher)
        {
            _scriptDir = scriptDir;
            _fileLockManager = fileLockManager;
            _outputTabManager = outputTabManager;
            _getSystemPrompt = getSystemPrompt;
            _getProjectDescription = getProjectDescription;
            _dispatcher = dispatcher;
        }

        public void StartProcess(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.GitStartHash = TaskLauncher.CaptureGitHead(task.ProjectPath);

            if (task.IsOvernight)
                TaskLauncher.PrepareTaskForOvernightStart(task);

            var fullPrompt = TaskLauncher.BuildFullPrompt(_getSystemPrompt(), task, _getProjectDescription(task));
            var projectPath = task.ProjectPath;

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            var claudeCmd = TaskLauncher.BuildClaudeCommand(task.SkipPermissions, task.RemoteSession, task.SpawnTeam);

            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(projectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            AppendOutput(task.Id, $"[UnityAgent] Task #{task.Id} starting...\n", activeTasks, historyTasks);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                AppendOutput(task.Id, $"[UnityAgent] Summary: {task.Summary}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[UnityAgent] Project: {projectPath}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[UnityAgent] Skip permissions: {task.SkipPermissions}\n", activeTasks, historyTasks);
            AppendOutput(task.Id, $"[UnityAgent] Remote session: {task.RemoteSession}\n", activeTasks, historyTasks);
            if (task.ExtendedPlanning)
                AppendOutput(task.Id, $"[UnityAgent] Extended planning: ON\n", activeTasks, historyTasks);
            if (task.IsOvernight)
            {
                AppendOutput(task.Id, $"[UnityAgent] Overnight mode: ON (max {task.MaxIterations} iterations, 12h cap)\n", activeTasks, historyTasks);
                AppendOutput(task.Id, $"[UnityAgent] Safety: skip-permissions forced, git blocked, 30min iteration timeout\n", activeTasks, historyTasks);
            }
            AppendOutput(task.Id, $"[UnityAgent] Connecting to Claude...\n\n", activeTasks, historyTasks);

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: false);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data).Trim();
                if (string.IsNullOrEmpty(line)) return;
                _dispatcher.BeginInvoke(() => ParseStreamJson(task.Id, line, activeTasks, historyTasks));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data);
                if (!string.IsNullOrWhiteSpace(line))
                    _dispatcher.BeginInvoke(() => AppendOutput(task.Id, $"[stderr] {line}\n", activeTasks, historyTasks));
            };

            process.Exited += (_, _) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }

                    CleanupScripts(task.Id);

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
                        task.Status = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                        task.EndTime = DateTime.Now;
                        AppendCompletionSummary(task, activeTasks, historyTasks);
                        AppendOutput(task.Id, $"\n[UnityAgent] Process finished (exit code: {exitCode}). " +
                            "Use Done/Cancel to close, or send a follow-up.\n", activeTasks, historyTasks);
                        _outputTabManager.UpdateTabHeader(task);
                    }

                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                });
            };

            try
            {
                process.Start();
                task.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

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
                            try { task.Process.Kill(true); } catch { }
                        }
                    };
                    iterationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[UnityAgent] ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        public void LaunchHeadless(AgentTask task)
        {
            var fullPrompt = TaskLauncher.BuildFullPrompt(_getSystemPrompt(), task, _getProjectDescription(task));
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

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing && task.Process is { HasExited: false })
            {
                try
                {
                    AppendOutput(task.Id, $"\n> {text}\n", activeTasks, historyTasks);
                    task.Process.StandardInput.WriteLine(text);
                    return;
                }
                catch { }
            }

            task.Status = AgentTaskStatus.Ongoing;
            task.EndTime = null;
            _outputTabManager.UpdateTabHeader(task);
            AppendOutput(task.Id, $"\n> {text}\n[UnityAgent] Sending follow-up with --continue...\n\n", activeTasks, historyTasks);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var followUpFile = Path.Combine(_scriptDir, $"followup_{task.Id}_{DateTime.Now.Ticks}.txt");
            File.WriteAllText(followUpFile, text, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"followup_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{followUpFile}'\n" +
                $"claude -p{skipFlag} --continue $prompt\n",
                Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{ps1File}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                _dispatcher.BeginInvoke(() => AppendOutput(task.Id, TaskLauncher.StripAnsi(e.Data) + "\n", activeTasks, historyTasks));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                _dispatcher.BeginInvoke(() => AppendOutput(task.Id, TaskLauncher.StripAnsi(e.Data) + "\n", activeTasks, historyTasks));
            };
            process.Exited += (_, _) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    var followUpExit = -1;
                    try { followUpExit = process.ExitCode; } catch { }
                    task.Status = followUpExit == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    AppendCompletionSummary(task, activeTasks, historyTasks);
                    AppendOutput(task.Id, "\n[UnityAgent] Follow-up complete.\n", activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                });
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[UnityAgent] Follow-up error: {ex.Message}\n", activeTasks, historyTasks);
            }
        }

        public void CancelTaskImmediate(AgentTask task)
        {
            if (task.IsFinished) return;

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
            _streamingToolState.Remove(task.Id);
        }

        public static void KillProcess(AgentTask task)
        {
            try
            {
                if (task.Process is { HasExited: false })
                    task.Process.Kill(true);
            }
            catch { }
        }

        public void RemoveStreamingState(string taskId) => _streamingToolState.Remove(taskId);

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
                                    AppendOutput(taskId, $"\n{FormatToolAction(toolName ?? "unknown", toolInput)}\n", activeTasks, historyTasks);

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
                                AppendOutput(taskId, $"\n{FormatToolAction(toolName ?? "tool", null)}...\n", activeTasks, historyTasks);

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
                                            _streamingToolState.Remove(taskId);
                                            _outputTabManager.UpdateTabHeader(activeTasks.FirstOrDefault(t => t.Id == taskId)!);
                                            return;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        _streamingToolState.Remove(taskId);
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

                    case "error":
                        var errMsg = root.TryGetProperty("error", out var err)
                            ? (err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString())
                            : "Unknown error";
                        AppendOutput(taskId, $"\n[Error] {errMsg}\n", activeTasks, historyTasks);
                        break;

                    default:
                        if (type != null && type != "ping" && type != "message_start" && type != "message_stop" && type != "user" && type != "system")
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
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose --continue --output-format stream-json $prompt";

            var ps1File = Path.Combine(_scriptDir, $"overnight_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: false);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data).Trim();
                if (string.IsNullOrEmpty(line)) return;
                _dispatcher.BeginInvoke(() => ParseStreamJson(task.Id, line, activeTasks, historyTasks));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = TaskLauncher.StripAnsi(e.Data);
                if (!string.IsNullOrWhiteSpace(line))
                    _dispatcher.BeginInvoke(() => AppendOutput(task.Id, $"[stderr] {line}\n", activeTasks, historyTasks));
            };

            process.Exited += (_, _) =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }
                    CleanupScripts(task.Id);
                    HandleOvernightIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
                });
            };

            try
            {
                process.Start();
                task.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

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
                        try { task.Process.Kill(true); } catch { }
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

        private void AppendOutput(string taskId, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _outputTabManager.AppendOutput(taskId, text, activeTasks, historyTasks);
        }

        private void AppendCompletionSummary(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var duration = (task.EndTime ?? DateTime.Now) - task.StartTime;
            var summary = TaskLauncher.GenerateCompletionSummary(
                task.ProjectPath, task.GitStartHash, task.Status, duration);
            task.CompletionSummary = summary;
            AppendOutput(task.Id, summary, activeTasks, historyTasks);
        }

        private void CleanupScripts(string taskId)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_scriptDir, $"*_{taskId}*"))
                    File.Delete(f);
            }
            catch { }
        }
    }
}
