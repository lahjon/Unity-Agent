using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HappyEngine.Dialogs;
using HappyEngine.Managers;
using HappyEngine.Models;

namespace HappyEngine
{
    public partial class MainWindow
    {
        // ── Toggle Helpers ─────────────────────────────────────────

        private void ResetPerTaskToggles()
        {
            RemoteSessionToggle.IsChecked = false;
            SpawnTeamToggle.IsChecked = false;
            OvernightToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;
            AutoDecomposeToggle.IsChecked = false;
        }

        /// <summary>Reads the main-window toggle controls into a <see cref="TaskConfigBase"/>.</summary>
        private void ReadUiFlagsInto(TaskConfigBase target)
        {
            target.RemoteSession = RemoteSessionToggle.IsChecked == true;
            target.SpawnTeam = SpawnTeamToggle.IsChecked == true;
            target.IsOvernight = OvernightToggle.IsChecked == true;
            target.ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true;
            target.PlanOnly = PlanOnlyToggle.IsChecked == true;
            target.IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true;
            target.UseMcp = UseMcpToggle.IsChecked == true;
            target.NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true;
            target.AutoDecompose = AutoDecomposeToggle.IsChecked == true;
        }

        /// <summary>Applies flags from a <see cref="TaskConfigBase"/> to the main-window toggle controls.</summary>
        private void ApplyFlagsToUi(TaskConfigBase source)
        {
            RemoteSessionToggle.IsChecked = source.RemoteSession;
            SpawnTeamToggle.IsChecked = source.SpawnTeam;
            OvernightToggle.IsChecked = source.IsOvernight;
            ExtendedPlanningToggle.IsChecked = source.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = source.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = source.IgnoreFileLocks;
            UseMcpToggle.IsChecked = source.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = source.NoGitWrite;
            AutoDecomposeToggle.IsChecked = source.AutoDecompose;
        }

        // ── Execute ────────────────────────────────────────────────

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            var desc = TaskInput.Text?.Trim();
            if (!TaskLauncher.ValidateTaskInput(desc)) return;

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
            {
                var modelTag = modelItem.Tag?.ToString();
                if (modelTag == "Gemini") selectedModel = ModelType.Gemini;
                else if (modelTag == "GeminiGameArt") selectedModel = ModelType.GeminiGameArt;
            }

            var task = TaskLauncher.CreateTask(
                desc!,
                _projectManager.ProjectPath,
                true,
                RemoteSessionToggle.IsChecked == true,
                false,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                PlanOnlyToggle.IsChecked == true,
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel,
                autoDecompose: AutoDecomposeToggle.IsChecked == true);
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);
            task.AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "";

            // Capture dependencies before clearing
            var dependencies = _pendingDependencies.ToList();
            ClearPendingDependencies();
            TaskInput.Clear();
            AdditionalInstructionsInput.Clear();

            ResetPerTaskToggles();

            if (selectedModel == ModelType.Gemini)
            {
                ExecuteGeminiTask(task);
                return;
            }

            if (selectedModel == ModelType.GeminiGameArt)
            {
                ExecuteGeminiGameArtTask(task);
                return;
            }

            if (task.Headless)
            {
                _taskExecutionManager.LaunchHeadless(task);
                UpdateStatus();
                return;
            }

            task.Summary = TaskLauncher.GenerateLocalSummary(desc!);
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            // Check if any dependencies are still active (not finished)
            var activeDeps = dependencies.Where(d => !d.IsFinished).ToList();
            if (activeDeps.Count > 0)
            {
                task.DependencyTaskIds = activeDeps.Select(d => d.Id).ToList();
                task.DependencyTaskNumbers = activeDeps.Select(d => d.TaskNumber).ToList();

                // Register with orchestrator so it tracks the DAG edges
                _taskOrchestrator.AddTask(task, task.DependencyTaskIds.ToList());

                if (!task.PlanOnly)
                {
                    // Start in plan mode first, then queue when planning completes
                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    task.Status = AgentTaskStatus.Planning;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Dependencies pending ({string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}) — starting in plan mode...\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else
                {
                    // User explicitly wants plan-only — queue as before
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Waiting for dependencies";
                    task.BlockedByTaskId = activeDeps[0].Id;
                    task.BlockedByTaskNumber = activeDeps[0].TaskNumber;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
            }
            else if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
            {
                // Max concurrent sessions reached — init-queue (no Claude session yet)
                task.Status = AgentTaskStatus.InitQueued;
                task.QueuedReason = "Max concurrent tasks reached";
                _outputTabManager.AppendOutput(task.Id,
                    $"[HappyEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            RefreshFilterCombos();
            UpdateStatus();
        }

        private void ExecuteGeminiTask(AgentTask task)
        {
            if (!_geminiService.IsConfigured)
            {
                Dialogs.DarkDialog.ShowConfirm(
                    "Gemini API key not configured.\n\nGo to Settings > Gemini tab to set your API key.\n" +
                    "Get one free at https://ai.google.dev/gemini-api/docs/api-key",
                    "Gemini Not Configured");
                return;
            }

            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            task.Summary = "Generating Image...";
            AddActiveTask(task);
            _outputTabManager.CreateGeminiTab(task, _geminiService.GetImageDirectory());

            _ = RunGeminiImageGeneration(task);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task RunGeminiImageGeneration(AgentTask task)
        {
            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;
            var progress = new Progress<string>(msg =>
            {
                _outputTabManager.AppendOutput(task.Id, msg, _activeTasks, _historyTasks);
            });

            _outputTabManager.AppendOutput(task.Id,
                $"[Gemini] Task #{task.TaskNumber} — Image generation\n" +
                $"[Gemini] Prompt: {task.Description}\n\n",
                _activeTasks, _historyTasks);

            try
            {
                var result = await _geminiService.GenerateImageAsync(task.Description, task.Id, progress, ct);

                if (result.Success)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime = DateTime.Now;
                    task.GeneratedImagePaths.AddRange(result.ImagePaths);

                    foreach (var path in result.ImagePaths)
                        _outputTabManager.AddGeminiImage(task.Id, path);

                    if (!string.IsNullOrEmpty(result.TextResponse))
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[Gemini] {result.TextResponse}\n", _activeTasks, _historyTasks);

                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[Gemini] Done — {result.ImagePaths.Count} image(s) generated.\n",
                        _activeTasks, _historyTasks);
                    task.Summary = $"Image: {(task.Description.Length > 30 ? task.Description[..30] + "..." : task.Description)}";
                }
                else
                {
                    task.Status = AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n{result.ErrorMessage}\n", _activeTasks, _historyTasks);
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    "\n[Gemini] Generation cancelled.\n", _activeTasks, _historyTasks);
            }
            catch (Exception ex)
            {
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    $"\n[Gemini] Unexpected error: {ex.Message}\n", _activeTasks, _historyTasks);
            }

            _outputTabManager.UpdateTabHeader(task);
            UpdateStatus();
        }

        // ── Game Art Generation ───────────────────────────────────

        private static readonly Dictionary<string, string> GameArtStylePrompts = new()
        {
            ["Sprite"] = "Create a 2D pixel art sprite with transparent background, suitable for a game character or object. " +
                         "Use a sprite sheet grid layout if multiple frames are needed. Sharp pixels, no anti-aliasing on edges. ",
            ["Texture"] = "Create a seamless tileable texture suitable for use as a game surface or material. " +
                          "The edges must tile perfectly when repeated. High detail, consistent lighting. ",
            ["UIIcon"] = "Create a clean 2D UI icon with transparent background for use in a game interface. " +
                         "Flat design, bold colors, clear silhouette, minimal detail. Suitable for buttons and HUD elements. ",
            ["Tilemap"] = "Create a tilemap sprite sheet with a uniform grid of tiles for a 2D game. " +
                          "Each tile should be the same size and seamlessly connect with adjacent tiles. " +
                          "Include ground, edges, corners, and transition tiles. Pixel art style, transparent background. "
        };

        private void ExecuteGeminiGameArtTask(AgentTask task)
        {
            if (!_geminiService.IsConfigured)
            {
                Dialogs.DarkDialog.ShowConfirm(
                    "Gemini API key not configured.\n\nGo to Settings > Gemini tab to set your API key.\n" +
                    "Get one free at https://ai.google.dev/gemini-api/docs/api-key",
                    "Gemini Not Configured");
                return;
            }

            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            // Determine selected asset type
            var assetType = "Sprite";
            if (AssetTypeCombo?.SelectedItem is ComboBoxItem assetItem)
                assetType = assetItem.Tag?.ToString() ?? "Sprite";

            task.Summary = $"Game Art ({assetType})...";
            AddActiveTask(task);
            _outputTabManager.CreateGeminiTab(task, _geminiService.GetImageDirectory());

            _ = RunGameAssetGeneration(task, assetType);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task RunGameAssetGeneration(AgentTask task, string assetType)
        {
            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;
            var progress = new Progress<string>(msg =>
            {
                _outputTabManager.AppendOutput(task.Id, msg, _activeTasks, _historyTasks);
            });

            // Build the enhanced prompt with game-art-specific modifiers
            var stylePrompt = GameArtStylePrompts.GetValueOrDefault(assetType, GameArtStylePrompts["Sprite"]);
            var enhancedPrompt = stylePrompt + task.Description;

            _outputTabManager.AppendOutput(task.Id,
                $"[Gemini Game Art] Task #{task.TaskNumber} — {assetType} generation\n" +
                $"[Gemini Game Art] User prompt: {task.Description}\n" +
                $"[Gemini Game Art] Asset type: {assetType}\n" +
                $"[Gemini Game Art] Full prompt: {enhancedPrompt}\n\n",
                _activeTasks, _historyTasks);

            try
            {
                var result = await _geminiService.GenerateImageAsync(enhancedPrompt, task.Id, progress, ct);

                if (result.Success)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime = DateTime.Now;
                    task.GeneratedImagePaths.AddRange(result.ImagePaths);

                    foreach (var path in result.ImagePaths)
                        _outputTabManager.AddGeminiImage(task.Id, path);

                    // Copy generated images to the active project's asset directory
                    var copiedPaths = CopyAssetsToProject(task, result.ImagePaths, assetType);

                    if (!string.IsNullOrEmpty(result.TextResponse))
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[Gemini Game Art] {result.TextResponse}\n", _activeTasks, _historyTasks);

                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[Gemini Game Art] Done — {result.ImagePaths.Count} image(s) generated.\n",
                        _activeTasks, _historyTasks);

                    if (copiedPaths.Count > 0)
                        _outputTabManager.AppendOutput(task.Id,
                            $"[Gemini Game Art] Copied {copiedPaths.Count} asset(s) to project.\n",
                            _activeTasks, _historyTasks);

                    task.Summary = $"Game Art ({assetType}): {(task.Description.Length > 25 ? task.Description[..25] + "..." : task.Description)}";
                }
                else
                {
                    task.Status = AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n{result.ErrorMessage}\n", _activeTasks, _historyTasks);
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    "\n[Gemini Game Art] Generation cancelled.\n", _activeTasks, _historyTasks);
            }
            catch (Exception ex)
            {
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    $"\n[Gemini Game Art] Unexpected error: {ex.Message}\n", _activeTasks, _historyTasks);
            }

            _outputTabManager.UpdateTabHeader(task);
            UpdateStatus();
        }

        private List<string> CopyAssetsToProject(AgentTask task, List<string> imagePaths, string assetType)
        {
            var copied = new List<string>();
            var projectPath = task.ProjectPath;

            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
            {
                _outputTabManager.AppendOutput(task.Id,
                    "[Gemini Game Art] Warning: No valid project path — skipping asset copy.\n",
                    _activeTasks, _historyTasks);
                return copied;
            }

            // Determine target subdirectory based on asset type
            var subDir = assetType switch
            {
                "Texture" => Path.Combine("Assets", "Textures"),
                "Tilemap" => Path.Combine("Assets", "Textures"),
                _ => Path.Combine("Assets", "Sprites")
            };

            var targetDir = Path.Combine(projectPath, subDir);

            try
            {
                Directory.CreateDirectory(targetDir);

                foreach (var srcPath in imagePaths)
                {
                    var fileName = Path.GetFileName(srcPath);
                    var destPath = Path.Combine(targetDir, fileName);

                    File.Copy(srcPath, destPath, overwrite: true);
                    copied.Add(destPath);
                    _outputTabManager.AppendOutput(task.Id,
                        $"[Gemini Game Art] Copied: {subDir}/{fileName}\n",
                        _activeTasks, _historyTasks);

                    // Create a companion .meta file with a unique GUID
                    CreateUnityMetaFile(destPath, task.Id);
                    _outputTabManager.AppendOutput(task.Id,
                        $"[Gemini Game Art] Created: {subDir}/{fileName}.meta\n",
                        _activeTasks, _historyTasks);
                }
            }
            catch (Exception ex)
            {
                _outputTabManager.AppendOutput(task.Id,
                    $"[Gemini Game Art] Error copying assets: {ex.Message}\n",
                    _activeTasks, _historyTasks);
            }

            return copied;
        }

        private static void CreateUnityMetaFile(string assetPath, string taskId)
        {
            var guid = Guid.NewGuid().ToString("N");
            var metaContent = $@"fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 12
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 0
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationDetail: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  applyGammaDecoding: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
  spritePackingTag:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
";
            File.WriteAllText(assetPath + ".meta", metaContent);
        }

        // ── Tab Events ─────────────────────────────────────────────

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => _outputTabManager.UpdateOutputTabWidths();

        private void OnTabCloseRequested(AgentTask task) => CloseTab(task);

        private void OnTabStoreRequested(AgentTask task)
        {
            // If the task is still active, cancel it first
            if (task.IsRunning || task.IsPlanning || task.IsPaused || task.IsQueued)
            {
                task.OvernightRetryTimer?.Stop();
                task.OvernightIterationTimer?.Stop();
                try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                TaskExecutionManager.KillProcess(task);
                task.Cts?.Dispose();
                task.Cts = null;
                _outputTabManager.AppendOutput(task.Id,
                    "\n[HappyEngine] Task cancelled and stored.\n", _activeTasks, _historyTasks);
            }

            // Create the stored task entry
            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                ProjectDisplayName = task.ProjectDisplayName,
                StoredPrompt = task.StoredPrompt ?? task.Description,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();

            // Clean up the active task and close the tab
            FinalizeTask(task);
        }

        private void OnTabResumeRequested(AgentTask task)
        {
            if (!task.IsFinished) return;

            // If the task was already moved to history, bring it back to active
            if (_historyTasks.Contains(task))
            {
                _historyTasks.Remove(task);
                var topRow = RootGrid.RowDefinitions[0];
                if (topRow.ActualHeight > 0)
                    topRow.Height = new GridLength(topRow.ActualHeight);
                _activeTasks.Insert(0, task);
                RestoreStarRow();
            }

            var resumeMethod = !string.IsNullOrEmpty(task.ConversationId) ? "--resume (session tracked)" : "--continue (no session ID)";
            _outputTabManager.AppendOutput(task.Id,
                $"\n[HappyEngine] Resumed session — type a follow-up message below. It will be sent with {resumeMethod}.\n",
                _activeTasks, _historyTasks);

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            UpdateStatus();
        }

        private void OnTabInputSent(AgentTask task, TextBox inputBox) =>
            _taskExecutionManager.SendInput(task, inputBox, _activeTasks, _historyTasks);

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.Paused)
            {
                _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
            }
            else if (task.IsRunning)
            {
                _taskExecutionManager.PauseTask(task);
                _outputTabManager.AppendOutput(task.Id, "\n[HappyEngine] Task paused.\n", _activeTasks, _historyTasks);
            }
        }

        private void ForceStartQueued_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.Status is not (AgentTaskStatus.Queued or AgentTaskStatus.InitQueued)) return;

            if (!DarkDialog.ShowConfirm(
                $"Force-start task #{task.TaskNumber}?\n\n" +
                $"This will bypass any dependencies or queue limits.\n\n" +
                $"Task: {task.ShortDescription}",
                "Force Start Queued Task"))
                return;

            if (task.Status == AgentTaskStatus.InitQueued)
            {
                task.QueuedReason = null;
                LaunchTaskProcess(task, $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (limit bypassed)...\n\n");
                UpdateStatus();
                return;
            }

            // Queued task
            if (task.DependencyTaskIdCount > 0)
            {
                _taskOrchestrator.MarkResolved(task.Id);
                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.ClearDependencyTaskIds();
                task.DependencyTaskNumbers.Clear();

                if (task.Process is { HasExited: false })
                {
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[HappyEngine] Force-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                        _activeTasks, _historyTasks);
                }
                else
                {
                    LaunchTaskProcess(task, $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (dependencies skipped)...\n\n");
                }
            }
            else
            {
                _fileLockManager.ForceStartQueuedTask(task);
            }

            _outputTabManager.UpdateTabHeader(task);
            UpdateStatus();
        }

        private void ToggleFileLock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            task.IgnoreFileLocks = !task.IgnoreFileLocks;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused or AgentTaskStatus.InitQueued || task.Status == AgentTaskStatus.Queued)
            {
                var processAlreadyDone = task.Process == null || task.Process.HasExited;
                if (processAlreadyDone)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime ??= DateTime.Now;
                }
                else
                {
                    if (!DarkDialog.ShowConfirm("This task is still running. Closing will terminate it.\n\nAre you sure?", "Task Running"))
                        return;

                    task.TokenLimitRetryTimer?.Stop();
                    task.TokenLimitRetryTimer = null;
                    task.Status = AgentTaskStatus.Cancelled;
                    task.EndTime = DateTime.Now;
                    TaskExecutionManager.KillProcess(task);
                }
            }
            else if (_activeTasks.Contains(task))
            {
                task.EndTime ??= DateTime.Now;
            }

            FinalizeTask(task);
        }

        // ── Task Actions ───────────────────────────────────────────

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.InitQueued)
            {
                // Force-start an init-queued task (bypass max concurrent limit)
                task.QueuedReason = null;
                LaunchTaskProcess(task, $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (limit bypassed)...\n\n");
                UpdateStatus();
                return;
            }

            if (task.Status == AgentTaskStatus.Queued)
            {
                if (task.DependencyTaskIdCount > 0)
                {
                    // Force-start a dependency-queued task — remove from orchestrator tracking
                    _taskOrchestrator.MarkResolved(task.Id);
                    task.QueuedReason = null;
                    task.BlockedByTaskId = null;
                    task.BlockedByTaskNumber = null;
                    task.ClearDependencyTaskIds();
                    task.DependencyTaskNumbers.Clear();

                    if (task.Process is { HasExited: false })
                    {
                        // Resume suspended process (was queued via drag-drop)
                        _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[HappyEngine] Force-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                            _activeTasks, _historyTasks);
                    }
                    else
                    {
                        LaunchTaskProcess(task, $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (dependencies skipped)...\n\n");
                    }

                    _outputTabManager.UpdateTabHeader(task);
                    UpdateStatus();
                }
                else
                {
                    _fileLockManager.ForceStartQueuedTask(task);
                }
                return;
            }

            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            TaskExecutionManager.KillProcess(task);
            _outputTabManager.UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void CopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!string.IsNullOrEmpty(task.Description))
                Clipboard.SetText(task.Description);
        }

        private void SetPriorityCritical_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Critical);
        private void SetPriorityHigh_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.High);
        private void SetPriorityNormal_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Normal);
        private void SetPriorityLow_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Low);

        private void SetTaskPriority(object sender, TaskPriority level)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            task.PriorityLevel = level;
            RecalculateQueuePriorities();
        }

        private async void RevertTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (string.IsNullOrEmpty(task.GitStartHash))
            {
                DarkDialog.ShowAlert("No git snapshot was captured when this task started.\nRevert is not available.", "Revert Unavailable");
                return;
            }

            if (string.IsNullOrEmpty(task.ProjectPath) || !Directory.Exists(task.ProjectPath))
            {
                DarkDialog.ShowAlert("The project path for this task no longer exists.", "Revert Unavailable");
                return;
            }

            var shortHash = task.GitStartHash.Length > 7 ? task.GitStartHash[..7] : task.GitStartHash;
            if (!DarkDialog.ShowConfirm(
                $"This will hard-reset the project to the state before this task ran (commit {shortHash}).\n\n" +
                "All uncommitted changes and any commits made after that point will be lost.\n\n" +
                "Are you sure?",
                "Revert Task Changes"))
                return;

            try
            {
                var result = await TaskLauncher.RunGitCommandAsync(
                    task.ProjectPath, $"reset --hard {task.GitStartHash}");

                if (result != null)
                {
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[HappyEngine] Reverted to commit {shortHash}.\n", _activeTasks, _historyTasks);
                    DarkDialog.ShowAlert($"Successfully reverted to commit {shortHash}.", "Revert Complete");
                }
                else
                {
                    DarkDialog.ShowAlert("Git reset failed. The commit may no longer exist or the repository state may have changed.", "Revert Failed");
                }
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            CancelTask(task, el);
        }

        private void TaskCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (e.ChangedButton == MouseButton.Middle)
            {
                CancelTask(task, el);
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Left && _outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            }
        }

        private void CancelTask(AgentTask task, FrameworkElement? sender = null)
        {
            if (task.IsFinished)
            {
                _outputTabManager.UpdateTabHeader(task);
                if (sender != null)
                {
                    _outputTabManager.AppendOutput(task.Id, "\n[HappyEngine] Task removed.\n", _activeTasks, _historyTasks);
                    AnimateRemoval(sender, () => MoveToHistory(task));
                }
                else
                {
                    MoveToHistory(task);
                }
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused)
            {
                if (!DarkDialog.ShowConfirm(
                    $"Task #{task.TaskNumber} is still running.\nAre you sure you want to cancel it?",
                    "Cancel Running Task"))
                    return;
            }

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
            if (task.TokenLimitRetryTimer != null)
            {
                task.TokenLimitRetryTimer.Stop();
                task.TokenLimitRetryTimer = null;
            }
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            TaskExecutionManager.KillProcess(task);
            task.Cts?.Dispose();
            task.Cts = null;
            _outputTabManager.AppendOutput(task.Id, "\n[HappyEngine] Task cancelled.\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            FinalizeTask(task);
        }

        private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            _outputTabManager.AppendOutput(task.Id, "\n[HappyEngine] Task removed.\n", _activeTasks, _historyTasks);
            AnimateRemoval(el, () =>
            {
                _outputTabManager.CloseTab(task);
                _historyTasks.Remove(task);
                _historyManager.SaveHistory(_historyTasks);
                RefreshFilterCombos();
                _outputTabManager.UpdateOutputTabWidths();
                UpdateStatus();
            });
        }

        private void ClearFinished_Click(object sender, RoutedEventArgs e)
        {
            var finished = _activeTasks.Where(t => t.IsFinished).ToList();
            if (finished.Count == 0) return;

            foreach (var task in finished)
                MoveToHistory(task);

            _outputTabManager.UpdateOutputTabWidths();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.ShowConfirm(
                $"Are you sure you want to clear all {_historyTasks.Count} history entries? This cannot be undone.",
                "Clear History")) return;

            foreach (var task in _historyTasks.ToList())
                _outputTabManager.CloseTab(task);
            _historyTasks.Clear();
            _historyManager.SaveHistory(_historyTasks);
            RefreshFilterCombos();
            _outputTabManager.UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (_outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
                return;
            }

            _outputTabManager.CreateTab(task);
            _outputTabManager.AppendOutput(task.Id, $"[HappyEngine] Resumed session\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[HappyEngine] Original task: {task.Description}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[HappyEngine] Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[HappyEngine] Status: {task.StatusText}\n", _activeTasks, _historyTasks);
            if (!string.IsNullOrEmpty(task.ConversationId))
                _outputTabManager.AppendOutput(task.Id, $"[HappyEngine] Session: {task.ConversationId}\n", _activeTasks, _historyTasks);
            var resumeMethod = !string.IsNullOrEmpty(task.ConversationId) ? "--resume (session tracked)" : "--continue (no session ID)";
            _outputTabManager.AppendOutput(task.Id, $"\n[HappyEngine] Type a follow-up message below. It will be sent with {resumeMethod}.\n", _activeTasks, _historyTasks);

            _historyTasks.Remove(task);
            var topRow = RootGrid.RowDefinitions[0];
            if (topRow.ActualHeight > 0)
                topRow.Height = new GridLength(topRow.ActualHeight);
            _activeTasks.Insert(0, task);
            RestoreStarRow();
            UpdateStatus();
        }

        private void RetryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!task.IsRetryable) return;
            RetryTask(task);
        }

        private void RerunTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: AgentTask task }) return;
            RetryTask(task);
        }

        private void StoreHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: AgentTask task }) return;
            if (!task.IsFinished) return;

            var output = task.OutputBuilder.ToString();

            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                ProjectDisplayName = task.ProjectDisplayName,
                StoredPrompt = !string.IsNullOrWhiteSpace(task.CompletionSummary) ? task.CompletionSummary : task.Description,
                FullOutput = output,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();
        }

        private async void VerifyTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!task.IsFinished) return;

            if (!_outputTabManager.HasTab(task.Id))
                _outputTabManager.CreateTab(task);

            _outputTabManager.AppendOutput(task.Id,
                "\n[HappyEngine] Running result verification...\n", _activeTasks, _historyTasks);

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);

            await _taskExecutionManager.RunResultVerificationAsync(task, _activeTasks, _historyTasks);
        }
    }
}
