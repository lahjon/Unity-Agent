using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// After task completion, updates the feature registry with changes detected during the task.
    /// Fire-and-forget — never blocks task teardown and never propagates exceptions.
    /// </summary>
    public class FeatureUpdateAgent
    {
        private const string UpdateJsonSchema =
            """{"type":"object","properties":{"updated_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"add_primary_files":{"type":"array","items":{"type":"string"}},"add_secondary_files":{"type":"array","items":{"type":"string"}},"remove_files":{"type":"array","items":{"type":"string"}},"updated_description":{"type":"string"}},"required":["id"]}},"new_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"keywords":{"type":"array","items":{"type":"string"}},"primary_files":{"type":"array","items":{"type":"string"}},"secondary_files":{"type":"array","items":{"type":"string"}}},"required":["id","name","description","keywords","primary_files"]}}},"required":["updated_features","new_features"]}""";

        private readonly FeatureRegistryManager _registryManager;

        public FeatureUpdateAgent(FeatureRegistryManager registryManager)
        {
            _registryManager = registryManager;
        }

        /// <summary>
        /// Analyzes task completion data and updates the feature registry accordingly.
        /// This method catches all exceptions internally — it is safe to call fire-and-forget.
        /// </summary>
        public async Task UpdateFeaturesAsync(
            string projectPath,
            string taskId,
            string taskDescription,
            string? completionSummary,
            List<string> changedFiles)
        {
            try
            {
                if (!_registryManager.RegistryExists(projectPath))
                    return;

                // Skip if there's nothing meaningful to analyze
                if (changedFiles.Count == 0 && string.IsNullOrWhiteSpace(completionSummary))
                    return;

                var index = await _registryManager.LoadIndexAsync(projectPath);
                if (index.Features.Count == 0 && changedFiles.Count == 0)
                    return;

                // Normalize changed file paths to relative
                var relativeFiles = changedFiles
                    .Select(f => ToRelativePath(f, projectPath))
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Build index JSON for the prompt
                var indexJson = JsonSerializer.Serialize(index.Features,
                    new JsonSerializerOptions { WriteIndented = false });

                // Load prompt template and format
                var template = PromptLoader.Load("FeatureUpdatePrompt.md");
                var prompt = string.Format(template,
                    taskDescription,
                    completionSummary ?? "No summary",
                    string.Join("\n", relativeFiles),
                    indexJson);

                // Call Haiku CLI for intelligent feature update analysis
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 1 --output-schema '{UpdateJsonSchema}'",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");

                using var process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(prompt.AsMemory(), default);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(default);
                await process.WaitForExitAsync(default);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                using var doc = JsonDocument.Parse(text);
                var wrapper = doc.RootElement;

                // The CLI with --output-format json wraps the result; extract the "result" field
                JsonElement root;
                if (wrapper.TryGetProperty("result", out var resultElement))
                {
                    if (resultElement.ValueKind == JsonValueKind.String)
                    {
                        using var innerDoc = JsonDocument.Parse(resultElement.GetString()!);
                        root = innerDoc.RootElement.Clone();
                    }
                    else
                    {
                        root = resultElement;
                    }
                }
                else
                {
                    root = wrapper;
                }

                // Process updated features
                if (root.TryGetProperty("updated_features", out var updatedArray))
                {
                    foreach (var item in updatedArray.EnumerateArray())
                    {
                        await ProcessUpdatedFeatureAsync(
                            projectPath, taskId, item);
                    }
                }

                // Process new features
                if (root.TryGetProperty("new_features", out var newArray))
                {
                    foreach (var item in newArray.EnumerateArray())
                    {
                        await ProcessNewFeatureAsync(projectPath, taskId, item);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureUpdateAgent", "Feature update failed (fire-and-forget)", ex);
            }
        }

        /// <summary>
        /// Applies incremental updates to an existing feature entry: adds/removes files,
        /// updates description, refreshes signatures for newly added primary files.
        /// </summary>
        private async Task ProcessUpdatedFeatureAsync(
            string projectPath, string taskId, JsonElement item)
        {
            var featureId = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(featureId))
                return;

            var feature = await _registryManager.LoadFeatureAsync(projectPath, featureId);
            if (feature is null)
                return;

            var newPrimaryFiles = new List<string>();

            // Add primary files
            if (item.TryGetProperty("add_primary_files", out var addPrimary))
            {
                foreach (var f in addPrimary.EnumerateArray())
                {
                    var path = f.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && !feature.PrimaryFiles.Contains(path))
                    {
                        feature.PrimaryFiles.Add(path);
                        newPrimaryFiles.Add(path);
                    }
                }
            }

            // Add secondary files
            if (item.TryGetProperty("add_secondary_files", out var addSecondary))
            {
                foreach (var f in addSecondary.EnumerateArray())
                {
                    var path = f.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && !feature.SecondaryFiles.Contains(path))
                        feature.SecondaryFiles.Add(path);
                }
            }

            // Remove files from both lists
            if (item.TryGetProperty("remove_files", out var removeFiles))
            {
                foreach (var f in removeFiles.EnumerateArray())
                {
                    var path = f.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    feature.PrimaryFiles.Remove(path);
                    feature.SecondaryFiles.Remove(path);
                    feature.Context.Signatures.Remove(path);
                }
            }

            // Update description if provided
            if (item.TryGetProperty("updated_description", out var descEl))
            {
                var desc = descEl.GetString();
                if (!string.IsNullOrWhiteSpace(desc))
                    feature.Description = desc;
            }

            // Re-extract signatures for newly added primary files
            foreach (var relPath in newPrimaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                var signatures = SignatureExtractor.ExtractSignatures(absPath);
                if (!string.IsNullOrEmpty(signatures))
                {
                    feature.Context.Signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = signatures
                    };
                }
            }

            // Sort file lists for deterministic output
            feature.PrimaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            feature.SecondaryFiles.Sort(StringComparer.OrdinalIgnoreCase);

            feature.TouchCount++;
            feature.LastUpdatedAt = DateTime.UtcNow;
            feature.LastUpdatedByTaskId = taskId;

            await _registryManager.SaveFeatureAsync(projectPath, feature);
        }

        /// <summary>
        /// Creates a brand-new feature entry from Haiku's analysis, extracts signatures
        /// for its primary files, and saves it to the registry.
        /// </summary>
        private async Task ProcessNewFeatureAsync(
            string projectPath, string taskId, JsonElement item)
        {
            var featureId = item.GetProperty("id").GetString();
            var name = item.GetProperty("name").GetString();
            var description = item.GetProperty("description").GetString();

            if (string.IsNullOrWhiteSpace(featureId) || string.IsNullOrWhiteSpace(name))
                return;

            var keywords = new List<string>();
            if (item.TryGetProperty("keywords", out var kwArray))
            {
                foreach (var kw in kwArray.EnumerateArray())
                {
                    var val = kw.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        keywords.Add(val);
                }
            }

            var primaryFiles = new List<string>();
            if (item.TryGetProperty("primary_files", out var pfArray))
            {
                foreach (var f in pfArray.EnumerateArray())
                {
                    var val = f.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        primaryFiles.Add(val);
                }
            }

            var secondaryFiles = new List<string>();
            if (item.TryGetProperty("secondary_files", out var sfArray))
            {
                foreach (var f in sfArray.EnumerateArray())
                {
                    var val = f.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        secondaryFiles.Add(val);
                }
            }

            var category = item.TryGetProperty("category", out var catEl)
                ? catEl.GetString() ?? ""
                : "";

            // Extract signatures for primary files
            var signatures = new Dictionary<string, FileSignature>();
            foreach (var relPath in primaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                var content = SignatureExtractor.ExtractSignatures(absPath);
                if (!string.IsNullOrEmpty(content))
                {
                    signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = content
                    };
                }
            }

            primaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            secondaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            keywords.Sort(StringComparer.OrdinalIgnoreCase);

            var feature = new FeatureEntry
            {
                Id = featureId,
                Name = name,
                Description = description ?? "",
                Category = category,
                Keywords = keywords,
                PrimaryFiles = primaryFiles,
                SecondaryFiles = secondaryFiles,
                Context = new FeatureContext { Signatures = signatures },
                TouchCount = 1,
                LastUpdatedAt = DateTime.UtcNow,
                LastUpdatedByTaskId = taskId
            };

            await _registryManager.SaveFeatureAsync(projectPath, feature);
        }

        /// <summary>
        /// Converts an absolute file path to a path relative to the project root.
        /// Returns the original path if it is already relative or cannot be made relative.
        /// </summary>
        private static string ToRelativePath(string filePath, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            // Normalize separators for consistent comparison
            var normalizedFile = filePath.Replace('\\', '/').TrimEnd('/');
            var normalizedProject = projectPath.Replace('\\', '/').TrimEnd('/');

            if (normalizedFile.StartsWith(normalizedProject + "/", StringComparison.OrdinalIgnoreCase))
                return normalizedFile[(normalizedProject.Length + 1)..];

            // Already relative or from a different root — return as-is
            return filePath.Replace('\\', '/');
        }
    }
}
