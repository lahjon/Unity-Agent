using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Gemini Image Generation ──────────────────────────────────

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
    }
}
