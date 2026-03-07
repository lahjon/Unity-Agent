using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// At task start, matches the task description to relevant features in the registry
    /// and builds a context block for prompt injection. Uses a fast Haiku call to confirm
    /// relevance and detect new features. Degrades gracefully — never blocks task launch.
    /// </summary>
    public class FeatureContextResolver
    {
        private const string ResolverJsonSchema =
            """{"type":"object","properties":{"relevant_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"confidence":{"type":"number"}},"required":["id","confidence"]}},"is_new_feature":{"type":"boolean"},"new_feature_name":{"type":"string"},"new_feature_id":{"type":"string"},"new_feature_keywords":{"type":"array","items":{"type":"string"}}},"required":["relevant_features","is_new_feature"]}""";

        private const double MinConfidenceThreshold = 0.3;
        private static readonly TimeSpan HaikuTimeout = TimeSpan.FromMinutes(2);

        private readonly FeatureRegistryManager _registryManager;

        public FeatureContextResolver(FeatureRegistryManager registryManager)
        {
            _registryManager = registryManager;
        }

        /// <summary>
        /// Resolves which features are relevant to the given task and builds a context block
        /// for prompt injection. Returns null if no registry exists, no features match, or
        /// an error occurs.
        /// </summary>
        public async Task<FeatureContextResult?> ResolveAsync(
            string projectPath, string taskDescription, CancellationToken ct = default)
        {
            try
            {
                if (!_registryManager.RegistryExists(projectPath))
                    return null;

                var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
                if (allFeatures.Count == 0)
                    return null;

                // Local keyword pre-filter to narrow candidates before the Haiku call
                var candidates = _registryManager.FindMatchingFeatures(
                    taskDescription, allFeatures, FeatureConstants.MaxFeaturesPerTask);

                bool isLikelyNewFeature = candidates.Count == 0;

                // Build candidates JSON for Haiku — even if empty, Haiku can still confirm new-feature
                var candidatesArray = candidates.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    description = f.Description,
                    keywords = f.Keywords
                });
                var candidatesJson = JsonSerializer.Serialize(candidatesArray,
                    new JsonSerializerOptions { WriteIndented = false });

                // Load prompt template and format
                var template = PromptLoader.Load("FeatureContextResolverPrompt.md");
                var prompt = string.Format(template, taskDescription, candidatesJson);

                // Call Haiku CLI for intelligent matching
                AppLogger.Info("FeatureContextResolver", $"Calling Haiku CLI for feature resolution. Prompt length: {prompt.Length} chars");

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 3 --json-schema \"{ResolverJsonSchema.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

                using var process = new Process { StartInfo = psi };
                process.Start();

                try
                {
                    await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                    process.StandardInput.Close();
                }
                catch (IOException ioEx)
                {
                    var earlyStderr = "";
                    try { earlyStderr = await process.StandardError.ReadToEndAsync(ct); } catch { }
                    AppLogger.Error("FeatureContextResolver",
                        $"Failed to write prompt to CLI stdin (pipe closed). stderr: {earlyStderr}", ioEx);
                    return null;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HaikuTimeout);

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLogger.Error("FeatureContextResolver",
                        $"Haiku CLI timed out after {HaikuTimeout.TotalMinutes:F0} minutes (PID: {process.Id}). Killing process.");
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var output = await stdoutTask;
                var stderr = await stderrTask;

                if (!string.IsNullOrWhiteSpace(stderr))
                    AppLogger.Warn("FeatureContextResolver", $"Haiku CLI stderr: {stderr[..Math.Min(500, stderr.Length)]}");

                if (process.ExitCode != 0)
                {
                    AppLogger.Error("FeatureContextResolver",
                        $"Haiku CLI exited with code {process.ExitCode}. stderr: {stderr[..Math.Min(1000, stderr.Length)]}");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    AppLogger.Warn("FeatureContextResolver", "Haiku CLI returned empty output");
                    return null;
                }

                AppLogger.Info("FeatureContextResolver", $"Haiku CLI completed. stdout length: {output.Length}");

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                using var doc = JsonDocument.Parse(text);
                var wrapper = doc.RootElement;

                // The CLI with --output-format json + --json-schema puts structured data
                // in "structured_output"; fall back to "result" for plain text responses
                JsonElement root;
                if (wrapper.TryGetProperty("structured_output", out var structured)
                    && structured.ValueKind == JsonValueKind.Object)
                {
                    root = structured;
                }
                else if (wrapper.TryGetProperty("result", out var resultElement))
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

                // Parse relevant features from the response
                var relevantFeatures = new List<MatchedFeature>();
                if (root.TryGetProperty("relevant_features", out var featuresArray))
                {
                    foreach (var item in featuresArray.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetString() ?? "";
                        var confidence = item.GetProperty("confidence").GetDouble();

                        if (confidence < MinConfidenceThreshold)
                            continue;

                        // Look up the full feature entry for the display name
                        var entry = allFeatures.FirstOrDefault(f =>
                            string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (entry is null)
                            continue;

                        relevantFeatures.Add(new MatchedFeature
                        {
                            FeatureId = entry.Id,
                            FeatureName = entry.Name,
                            Confidence = confidence
                        });
                    }
                }

                // Order by confidence descending
                relevantFeatures = relevantFeatures
                    .OrderByDescending(f => f.Confidence)
                    .ToList();

                // Refresh stale signatures for confirmed features before building context
                var confirmedEntries = relevantFeatures
                    .Select(mf => allFeatures.FirstOrDefault(f => f.Id == mf.FeatureId))
                    .Where(e => e is not null)
                    .Cast<FeatureEntry>()
                    .ToList();

                foreach (var entry in confirmedEntries)
                    await _registryManager.RefreshStaleSignaturesAsync(projectPath, entry);

                // Build the context block for prompt injection
                var contextBlock = _registryManager.BuildFeatureContextBlock(confirmedEntries);

                // Check if Haiku identified a new feature
                var isNewFeature = root.TryGetProperty("is_new_feature", out var newFlag) && newFlag.GetBoolean();
                string? suggestedName = null;
                List<string>? suggestedKeywords = null;

                if (isNewFeature)
                {
                    suggestedName = root.TryGetProperty("new_feature_name", out var nameEl)
                        ? nameEl.GetString()
                        : null;

                    var newFeatureId = root.TryGetProperty("new_feature_id", out var idEl)
                        ? idEl.GetString()
                        : null;

                    if (root.TryGetProperty("new_feature_keywords", out var kwArray))
                    {
                        suggestedKeywords = new List<string>();
                        foreach (var kw in kwArray.EnumerateArray())
                        {
                            var val = kw.GetString();
                            if (!string.IsNullOrWhiteSpace(val))
                                suggestedKeywords.Add(val);
                        }
                    }

                    // Create a placeholder feature entry so the update agent can fill it in later
                    if (!string.IsNullOrWhiteSpace(newFeatureId) && !string.IsNullOrWhiteSpace(suggestedName))
                    {
                        var placeholder = new FeatureEntry
                        {
                            Id = newFeatureId,
                            Name = suggestedName,
                            Description = $"New feature identified from task: {taskDescription}",
                            Keywords = suggestedKeywords ?? new List<string>(),
                            LastUpdatedAt = DateTime.UtcNow
                        };
                        await _registryManager.SaveFeatureAsync(projectPath, placeholder);
                    }
                }

                return new FeatureContextResult
                {
                    RelevantFeatures = relevantFeatures,
                    IsNewFeature = isNewFeature,
                    SuggestedNewFeatureName = suggestedName,
                    SuggestedKeywords = suggestedKeywords,
                    ContextBlock = contextBlock
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureContextResolver", $"Feature resolution failed, skipping context injection: {ex.Message}", ex);
                return null;
            }
        }
    }
}
