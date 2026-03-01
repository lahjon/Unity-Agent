using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            FeatureModeToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;
            AutoDecomposeToggle.IsChecked = false;
            ApplyFixToggle.IsChecked = true;
            if (FeatureModeIterationsPanel != null)
                FeatureModeIterationsPanel.Visibility = Visibility.Collapsed;
            if (FeatureModeIterationsBox != null)
                FeatureModeIterationsBox.Text = "2";
        }

        /// <summary>Reads the main-window toggle controls into a <see cref="TaskConfigBase"/>.</summary>
        private void ReadUiFlagsInto(TaskConfigBase target)
        {
            target.RemoteSession = RemoteSessionToggle.IsChecked == true;
            target.SpawnTeam = SpawnTeamToggle.IsChecked == true;
            target.IsFeatureMode = FeatureModeToggle.IsChecked == true;
            target.ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true;
            target.PlanOnly = PlanOnlyToggle.IsChecked == true;
            target.IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true;
            target.UseMcp = UseMcpToggle.IsChecked == true;
            target.NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true;
            target.AutoDecompose = AutoDecomposeToggle.IsChecked == true;
            target.ApplyFix = ApplyFixToggle.IsChecked == true;
            if (int.TryParse(FeatureModeIterationsBox?.Text, out var iter) && iter > 0)
                target.FeatureModeIterations = iter;
        }

        /// <summary>Applies flags from a <see cref="TaskConfigBase"/> to the main-window toggle controls.</summary>
        private void ApplyFlagsToUi(TaskConfigBase source)
        {
            RemoteSessionToggle.IsChecked = source.RemoteSession;
            SpawnTeamToggle.IsChecked = source.SpawnTeam;
            FeatureModeToggle.IsChecked = source.IsFeatureMode;
            ExtendedPlanningToggle.IsChecked = source.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = source.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = source.IgnoreFileLocks;
            UseMcpToggle.IsChecked = source.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = source.NoGitWrite;
            AutoDecomposeToggle.IsChecked = source.AutoDecompose;
            ApplyFixToggle.IsChecked = source.ApplyFix;
            if (FeatureModeIterationsPanel != null)
                FeatureModeIterationsPanel.Visibility = source.IsFeatureMode ? Visibility.Visible : Visibility.Collapsed;
            if (FeatureModeIterationsBox != null)
                FeatureModeIterationsBox.Text = source.FeatureModeIterations.ToString();
        }

        // ── Execute ────────────────────────────────────────────────

        /// <summary>
        /// Creates an <see cref="AgentTask"/> from a description using the current UI toggle
        /// state, sets project metadata, then routes through the appropriate launch pipeline
        /// (Gemini image gen, headless, or standard terminal via <see cref="LaunchTask"/>).
        /// </summary>
        /// <remarks>
        /// Callers must read any UI state they need (model combo, additional instructions, etc.)
        /// <b>before</b> calling this method, because it reads toggle values internally.
        /// <see cref="ResetPerTaskToggles"/> should be called <b>after</b> this method returns.
        /// </remarks>
        private void LaunchTaskFromDescription(
            string description,
            string summary,
            ModelType model = ModelType.ClaudeCode,
            List<string>? imagePaths = null,
            bool planOnly = false,
            List<AgentTask>? dependencies = null,
            string? additionalInstructions = null)
        {
            var task = _taskFactory.CreateTask(
                description,
                _projectManager.ProjectPath,
                skipPermissions: true,
                remoteSession: RemoteSessionToggle.IsChecked == true,
                headless: false,
                isFeatureMode: FeatureModeToggle.IsChecked == true,
                ignoreFileLocks: IgnoreFileLocksToggle.IsChecked == true,
                useMcp: UseMcpToggle.IsChecked == true,
                spawnTeam: SpawnTeamToggle.IsChecked == true,
                extendedPlanning: ExtendedPlanningToggle.IsChecked == true,
                noGitWrite: DefaultNoGitWriteToggle.IsChecked == true,
                planOnly: planOnly,
                imagePaths: imagePaths,
                model: model,
                autoDecompose: AutoDecomposeToggle.IsChecked == true,
                applyFix: ApplyFixToggle.IsChecked == true);

            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);
            task.Summary = summary;
            task.AdditionalInstructions = additionalInstructions ?? "";

            if (task.IsFeatureMode && int.TryParse(FeatureModeIterationsBox?.Text, out var iterations) && iterations > 0)
                task.MaxIterations = iterations;

            if (model == ModelType.Gemini)
            {
                ExecuteGeminiTask(task);
                return;
            }

            if (model == ModelType.GeminiGameArt)
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

            LaunchTask(task, dependencies);
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            var desc = TaskInput.Text?.Trim();
            if (!_taskFactory.ValidateTaskInput(desc)) return;

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
            {
                var modelTag = modelItem.Tag?.ToString();
                if (modelTag == "Gemini") selectedModel = ModelType.Gemini;
                else if (modelTag == "GeminiGameArt") selectedModel = ModelType.GeminiGameArt;
            }

            // Capture UI state before clearing
            var additionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "";
            var imagePaths = _imageManager.DetachImages();
            var dependencies = _pendingDependencies.ToList();
            ClearPendingDependencies();
            TaskInput.Clear();
            AdditionalInstructionsInput.Clear();

            LaunchTaskFromDescription(
                desc!,
                _taskFactory.GenerateLocalSummary(desc!),
                selectedModel,
                imagePaths,
                PlanOnlyToggle.IsChecked == true,
                dependencies,
                additionalInstructions);

            ResetPerTaskToggles();
        }

        private async void ComposeWorkflow_Click(object sender, RoutedEventArgs e)
        {
            var result = await WorkflowComposerDialog.ShowAsync(_claudeService, _projectManager.ProjectPath);
            if (result == null || result.Steps.Count == 0)
                return;

            // Map taskName -> AgentTask for dependency resolution
            var tasksByName = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in result.Steps)
            {
                var task = _taskFactory.CreateTask(
                    step.Description,
                    _projectManager.ProjectPath,
                    skipPermissions: true,
                    remoteSession: false,
                    headless: false,
                    isFeatureMode: false,
                    ignoreFileLocks: IgnoreFileLocksToggle.IsChecked == true,
                    useMcp: UseMcpToggle.IsChecked == true,
                    noGitWrite: DefaultNoGitWriteToggle.IsChecked == true);

                task.Summary = step.TaskName;
                task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
                task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);

                // Resolve dependencies from name to task ID
                var depIds = new List<string>();
                var depNumbers = new List<int>();
                foreach (var depName in step.DependsOn)
                {
                    if (tasksByName.TryGetValue(depName, out var depTask))
                    {
                        depIds.Add(depTask.Id);
                        depNumbers.Add(depTask.TaskNumber);
                    }
                }

                tasksByName[step.TaskName] = task;

                // Add to UI
                AddActiveTask(task);
                _outputTabManager.CreateTab(task);

                if (depIds.Count > 0)
                {
                    task.DependencyTaskIds = depIds;
                    task.DependencyTaskNumbers = depNumbers;
                    _taskOrchestrator.AddTask(task, depIds);

                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    task.Status = AgentTaskStatus.Planning;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Workflow task \"{step.TaskName}\" — waiting for dependencies: {string.Join(", ", step.DependsOn)}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
                {
                    _taskOrchestrator.AddTask(task, depIds);
                    task.Status = AgentTaskStatus.InitQueued;
                    task.QueuedReason = "Max concurrent tasks reached";
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Workflow task \"{step.TaskName}\" — queued (max concurrent tasks reached)\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
                else
                {
                    _taskOrchestrator.AddTask(task, depIds);
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Workflow task \"{step.TaskName}\" — starting...\n",
                        _activeTasks, _historyTasks);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
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
            using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                task.Cts?.Token ?? System.Threading.CancellationToken.None, _windowCts.Token);
            var ct = linkedCts.Token;
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
            using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                task.Cts?.Token ?? System.Threading.CancellationToken.None, _windowCts.Token);
            var ct = linkedCts.Token;
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
                task.FeatureModeRetryTimer?.Stop();
                task.FeatureModeIterationTimer?.Stop();
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
                UpdateQueuePositions();
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
                UpdateQueuePositions();
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
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (!string.IsNullOrEmpty(task.Description))
                Clipboard.SetText(task.Description);
        }

        private void SetPriorityCritical_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Critical);
        private void SetPriorityHigh_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.High);
        private void SetPriorityNormal_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Normal);
        private void SetPriorityLow_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Low);

        private static AgentTask? GetTaskFromContextMenuItem(object sender)
        {
            // Direct DataContext binding (works when item itself has the context)
            if (sender is FrameworkElement { DataContext: AgentTask task })
                return task;

            // Walk up the logical tree from the MenuItem to find the ContextMenu,
            // then resolve PlacementTarget (the card that was right-clicked).
            if (sender is MenuItem mi)
            {
                DependencyObject? current = mi;
                while (current != null)
                {
                    if (current is ContextMenu ctx && ctx.PlacementTarget is FrameworkElement target
                                                   && target.DataContext is AgentTask t)
                        return t;
                    current = LogicalTreeHelper.GetParent(current);
                }
            }

            return null;
        }

        private void SetTaskPriority(object sender, TaskPriority level)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;

            task.PriorityLevel = level;
            ReorderByPriority();
        }

        private async void RevertTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;

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
                var result = await _gitHelper.RunGitCommandAsync(
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

                // Re-check after dialog: the modal dialog has its own message pump,
                // so the process exit callback may have fired and changed the task state.
                if (task.IsFinished)
                {
                    _outputTabManager.UpdateTabHeader(task);
                    if (!_activeTasks.Contains(task))
                        return;
                    MoveToHistory(task);
                    return;
                }
            }

            if (task.FeatureModeRetryTimer != null)
            {
                task.FeatureModeRetryTimer.Stop();
                task.FeatureModeRetryTimer = null;
            }
            if (task.FeatureModeIterationTimer != null)
            {
                task.FeatureModeIterationTimer.Stop();
                task.FeatureModeIterationTimer = null;
            }
            if (task.TokenLimitRetryTimer != null)
            {
                task.TokenLimitRetryTimer.Stop();
                task.TokenLimitRetryTimer = null;
            }
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            // Capture the process reference before clearing state, then kill on a
            // background thread — Process.Kill(entireProcessTree: true) can block
            // while enumerating/terminating child processes on Windows.
            var proc = task.Process;
            task.Process = null;
            if (proc is { HasExited: false })
                System.Threading.Tasks.Task.Run(() => { try { proc.Kill(true); } catch { /* best-effort */ } });
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
            if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                _outputTabManager.AppendOutput(task.Id, $"\n{task.CompletionSummary}\n", _activeTasks, _historyTasks);
            if (!string.IsNullOrWhiteSpace(task.Recommendations))
                _outputTabManager.AppendOutput(task.Id, $"\n[Recommendations]\n{task.Recommendations}\n", _activeTasks, _historyTasks);
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

        private async void CommitTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: AgentTask task }) return;
            if (!task.IsFinished) return;

            if (task.IsCommitted)
            {
                // Uncommit - just mark as uncommitted
                task.IsCommitted = false;
                task.CommitHash = null;
                _historyManager.SaveHistory(_historyTasks);
                return;
            }

            // Commit the task changes
            var (success, errorMessage) = await CommitTaskAsync(task);
            if (!success)
            {
                MessageBox.Show($"Failed to commit changes for task #{task.TaskNumber}\n\nError: {errorMessage}", "Commit Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Commits task changes to git. Can be called from UI or auto-commit.
        /// </summary>
        /// <param name="task">The task to commit</param>
        /// <returns>Tuple of (success, errorMessage) - success is true if commit was successful, errorMessage contains details if failed</returns>
        public async Task<(bool success, string? errorMessage)> CommitTaskAsync(AgentTask task)
        {
            if (task == null || !task.IsFinished || task.IsCommitted || task.NoGitWrite)
            {
                if (task == null) return (false, "Task is null");
                if (!task.IsFinished) return (false, "Task is not finished");
                if (task.IsCommitted) return (false, "Task is already committed");
                if (task.NoGitWrite) return (false, "Task has NoGitWrite flag set");
                return (false, "Unknown pre-condition failure");
            }

            try
            {
                var summary = !string.IsNullOrWhiteSpace(task.Summary) ? task.Summary : task.Description;
                var commitMessage = $"Task #{task.TaskNumber}: {summary}";

                // Get the files locked by this task for scoped commit
                var lockedFiles = task.Runtime.LockedFilesForCommit;
                if (lockedFiles == null || lockedFiles.Count == 0)
                {
                    // No files locked by this task, nothing to commit
                    return (false, "No files were modified by this task to commit");
                }

                // Build relative paths from the project root for the git commands
                var projectRoot = task.ProjectPath.TrimEnd('\\', '/').ToLowerInvariant() + "\\";
                var relativePaths = new List<string>();
                foreach (var absPath in lockedFiles)
                {
                    var rel = absPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                        ? absPath.Substring(projectRoot.Length)
                        : absPath;

                    // Validate against path traversal attacks
                    if (rel.Contains("..") || Path.IsPathRooted(rel))
                    {
                        AppLogger.Warn("TaskExecution", $"Rejected suspicious path during git operation: {rel}");
                        continue;
                    }

                    relativePaths.Add(rel.Replace('\\', '/'));
                }

                // Serialize git operations to prevent concurrent commits from racing
                await TaskExecutionManager.GitCommitSemaphore.WaitAsync();
                try
                {
                    // Stage only the locked files (handles new/untracked files)
                    var pathArgs = string.Join(" ", relativePaths.Select(p => $"\"{p}\""));
                    var addResult = await _gitHelper.RunGitCommandAsync(task.ProjectPath, $"add -- {pathArgs}");

                    // Check if git add failed
                    if (addResult == null)
                    {
                        Debug.WriteLine($"CommitTaskAsync failed: Failed to stage files");
                        return (false, $"Failed to stage files for commit. Git add command failed for paths: {pathArgs}");
                    }

                    // Commit only these specific files — the pathspec ensures no other
                    // staged changes from concurrent tasks leak into this commit
                    var result = await _gitHelper.RunGitCommandAsync(
                        task.ProjectPath,
                        $"commit -F - -- {pathArgs}", commitMessage);

                    if (result != null)
                    {
                        // Get the commit hash
                        var hash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath);
                        if (hash != null)
                        {
                            task.IsCommitted = true;
                            task.CommitHash = hash;
                            _historyManager.SaveHistory(_historyTasks);

                            // Mark git panel as dirty to refresh
                            _gitPanelManager?.MarkDirty();

                            // Release deferred file locks if any
                            ReleaseTaskLocksAfterCommit(task);

                            return (true, null);
                        }
                        else
                        {
                            return (false, "Git commit succeeded but failed to capture commit hash");
                        }
                    }
                    else
                    {
                        return (false, "Git commit command failed. This could be due to no changes to commit, pre-commit hooks failing, or other git issues");
                    }
                }
                finally
                {
                    TaskExecutionManager.GitCommitSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitTaskAsync failed: {ex.Message}");
                return (false, $"Unexpected error during commit: {ex.Message}");
            }
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

            var previousStatus = task.Status;
            task.Status = AgentTaskStatus.Verifying;
            _outputTabManager.UpdateTabHeader(task);

            await _taskExecutionManager.RunResultVerificationAsync(task, _activeTasks, _historyTasks);

            task.Status = previousStatus;
            _outputTabManager.UpdateTabHeader(task);
        }
    }
}
