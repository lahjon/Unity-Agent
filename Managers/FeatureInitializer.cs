using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers;

/// <summary>
/// Bootstraps a full feature registry for a project by scanning all source files,
/// extracting signatures, and using a Sonnet LLM call to identify logical features.
/// </summary>
public class FeatureInitializer
{
    private readonly FeatureRegistryManager _registryManager;

    /// <summary>Reports progress status messages to the UI.</summary>
    public event Action<string>? ProgressChanged;

    public FeatureInitializer(FeatureRegistryManager registryManager)
    {
        _registryManager = registryManager;
    }

    // ── Schema for the Sonnet output ────────────────────────────────────

    private const string OutputSchema =
        """{"type":"object","properties":{"features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"keywords":{"type":"array","items":{"type":"string"}},"primary_files":{"type":"array","items":{"type":"string"}},"secondary_files":{"type":"array","items":{"type":"string"}},"related_feature_ids":{"type":"array","items":{"type":"string"}},"key_types":{"type":"array","items":{"type":"string"}},"patterns":{"type":"array","items":{"type":"string"}},"dependencies":{"type":"array","items":{"type":"string"}}},"required":["id","name","description","category","keywords","primary_files"]}}},"required":["features"]}""";

    // ── JSON deserialization models for the Sonnet response ──────────────

    private sealed class SonnetResponse
    {
        [JsonPropertyName("features")]
        public List<SonnetFeature> Features { get; set; } = [];
    }

    private sealed class SonnetFeature
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = [];

        [JsonPropertyName("primary_files")]
        public List<string> PrimaryFiles { get; set; } = [];

        [JsonPropertyName("secondary_files")]
        public List<string> SecondaryFiles { get; set; } = [];

        [JsonPropertyName("related_feature_ids")]
        public List<string> RelatedFeatureIds { get; set; } = [];

        [JsonPropertyName("key_types")]
        public List<string> KeyTypes { get; set; } = [];

        [JsonPropertyName("patterns")]
        public List<string> Patterns { get; set; } = [];

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = [];
    }

    // ── Main entry point ────────────────────────────────────────────────

    /// <summary>
    /// Scans the project, calls Sonnet to identify features, builds and persists
    /// the feature registry, and sets up git integration files.
    /// Returns the final <see cref="FeatureIndex"/>, or <c>null</c> on failure.
    /// </summary>
    public async Task<FeatureIndex?> InitializeAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            // ── Phase 1: File Discovery ─────────────────────────────────
            ReportProgress("Scanning project files...");

            var projectType = DetectProjectType(projectPath);
            var relativeFiles = ScanSourceFiles(projectPath);

            ct.ThrowIfCancellationRequested();
            ReportProgress($"Found {relativeFiles.Count} source files");

            if (relativeFiles.Count == 0)
            {
                ReportProgress("No supported source files found — nothing to initialize");
                return null;
            }

            // ── Phase 2: Build Structural Map ───────────────────────────
            ReportProgress("Extracting code signatures...");

            var directoryTree = BuildDirectoryTree(projectPath, relativeFiles);
            var signaturesBuilder = new StringBuilder();

            var filesToProcess = relativeFiles
                .Take(FeatureConstants.MaxFilesPerSonnetChunk)
                .ToList();

            foreach (var relativePath in filesToProcess)
            {
                ct.ThrowIfCancellationRequested();

                var absolutePath = Path.Combine(projectPath, relativePath);
                var signatures = SignatureExtractor.ExtractSignatures(absolutePath);

                if (!string.IsNullOrEmpty(signatures))
                {
                    signaturesBuilder.AppendLine($"## {relativePath}");
                    signaturesBuilder.AppendLine(signatures);
                    signaturesBuilder.AppendLine();
                }
            }

            var signaturesText = signaturesBuilder.ToString();

            // ── Phase 3: LLM Analysis ───────────────────────────────────
            ReportProgress("Analyzing project structure with AI...");

            var template = PromptLoader.Load("FeatureInitializationPrompt.md");
            var prompt = string.Format(template, projectType, directoryTree, signaturesText);

            ct.ThrowIfCancellationRequested();

            var sonnetResponse = await CallSonnetAsync(prompt, ct);
            if (sonnetResponse is null || sonnetResponse.Features.Count == 0)
            {
                ReportProgress("AI analysis returned no features — initialization aborted");
                return null;
            }

            // ── Phase 4: Registry Creation ──────────────────────────────
            ReportProgress("Building feature registry...");

            var createdCount = 0;
            foreach (var sf in sonnetResponse.Features)
            {
                ct.ThrowIfCancellationRequested();

                var feature = new FeatureEntry
                {
                    Id = sf.Id,
                    Name = sf.Name,
                    Description = sf.Description,
                    Category = sf.Category,
                    Keywords = sf.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                    PrimaryFiles = sf.PrimaryFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    SecondaryFiles = (sf.SecondaryFiles ?? []).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    RelatedFeatureIds = (sf.RelatedFeatureIds ?? []).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    Context = new FeatureContext
                    {
                        KeyTypes = (sf.KeyTypes ?? []).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                        Patterns = (sf.Patterns ?? []).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                        Dependencies = (sf.Dependencies ?? []).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                    },
                    LastUpdatedAt = DateTime.UtcNow,
                    TouchCount = 0
                };

                // Populate signatures for primary files
                foreach (var primaryFile in feature.PrimaryFiles)
                {
                    var absolutePath = Path.Combine(projectPath, primaryFile);
                    var signatures = SignatureExtractor.ExtractSignatures(absolutePath);
                    var hash = SignatureExtractor.ComputeFileHash(absolutePath);

                    if (!string.IsNullOrEmpty(signatures))
                    {
                        feature.Context.Signatures[primaryFile] = new FileSignature
                        {
                            Hash = hash,
                            Content = signatures
                        };
                    }
                }

                await _registryManager.SaveFeatureAsync(projectPath, feature);
                createdCount++;
            }

            ReportProgress($"Created {createdCount} features");

            // ── Phase 5: Git Integration ────────────────────────────────
            EnsureGitIntegration(projectPath);

            ReportProgress($"Feature registry initialized with {createdCount} features");

            return await _registryManager.LoadIndexAsync(projectPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Initialization failed: {ex.Message}", ex);
            ReportProgress($"Initialization failed: {ex.Message}");
            return null;
        }
    }

    // ── Helper: Scan Source Files ────────────────────────────────────────

    /// <summary>
    /// Returns relative paths of all source files under <paramref name="projectPath"/>,
    /// skipping directories listed in <see cref="FeatureConstants.IgnoredDirectories"/>.
    /// </summary>
    public List<string> ScanSourceFiles(string projectPath)
    {
        var supportedExtensions = SignatureExtractor.GetSupportedExtensions();
        var ignoredDirs = new HashSet<string>(FeatureConstants.IgnoredDirectories, StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!supportedExtensions.Contains(ext))
                    continue;

                // Check if any path segment is in the ignored set
                var relativePath = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
                var segments = relativePath.Split('/');

                var skip = false;
                for (var i = 0; i < segments.Length - 1; i++) // skip the filename itself
                {
                    if (ignoredDirs.Contains(segments[i]))
                    {
                        skip = true;
                        break;
                    }
                }

                if (!skip)
                    results.Add(relativePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Error scanning files: {ex.Message}", ex);
        }

        return results;
    }

    // ── Helper: Build Directory Tree ────────────────────────────────────

    /// <summary>
    /// Groups files by their first directory component and returns a compact
    /// tree summary (e.g. "  Managers/ (25 files)").
    /// </summary>
    public string BuildDirectoryTree(string projectPath, List<string> relativeFiles)
    {
        var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in relativeFiles)
        {
            var firstSlash = file.IndexOf('/');
            var dir = firstSlash >= 0 ? file[..firstSlash] : "(root)";

            if (!groups.TryAdd(dir, 1))
                groups[dir]++;
        }

        var sb = new StringBuilder();
        foreach (var (dir, count) in groups)
        {
            if (dir == "(root)")
                sb.AppendLine($"  (root files) ({count} files)");
            else
                sb.AppendLine($"  {dir}/ ({count} files)");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Helper: Detect Project Type ─────────────────────────────────────

    /// <summary>
    /// Checks for well-known project indicator files and returns a string
    /// describing the project type.
    /// </summary>
    public string DetectProjectType(string projectPath)
    {
        var hasCsproj = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories).Any();
        var hasAssetsFolder = Directory.Exists(Path.Combine(projectPath, "Assets"));

        if (hasCsproj && hasAssetsFolder)
            return "Unity C#";

        if (hasCsproj)
            return "C# .NET";

        if (File.Exists(Path.Combine(projectPath, "project.godot")))
            return "GDScript/Godot";

        if (File.Exists(Path.Combine(projectPath, "package.json")))
            return "TypeScript/JavaScript";

        if (File.Exists(Path.Combine(projectPath, "requirements.txt")) ||
            File.Exists(Path.Combine(projectPath, "setup.py")) ||
            File.Exists(Path.Combine(projectPath, "pyproject.toml")))
            return "Python";

        return "Mixed";
    }

    // ── Helper: Call Sonnet via Claude Code CLI ─────────────────────────

    private async Task<SonnetResponse?> CallSonnetAsync(string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = $"-p --output-format json --model {AppConstants.ClaudeSonnet} --max-turns 1 --output-schema '{OutputSchema}'",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.Environment.Remove("CLAUDECODE");

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            // Write the prompt to stdin and close it
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            // Read stdout and stderr concurrently to avoid deadlocks
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                AppLogger.Debug("FeatureInitializer", $"Claude CLI exited with code {process.ExitCode}. stderr: {stderr}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                AppLogger.Debug("FeatureInitializer", "Claude CLI returned empty output");
                return null;
            }

            // The CLI with --output-format json wraps the result; extract the "result" field
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            string resultJson;
            if (root.TryGetProperty("result", out var resultElement))
            {
                // result may be a string containing JSON or an object directly
                resultJson = resultElement.ValueKind == JsonValueKind.String
                    ? resultElement.GetString()!
                    : resultElement.GetRawText();
            }
            else
            {
                // Fallback: treat the whole output as the response
                resultJson = stdout;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SonnetResponse>(resultJson, options);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Error calling Claude CLI: {ex.Message}", ex);
            return null;
        }
    }

    // ── Helper: Git Integration ─────────────────────────────────────────

    private void EnsureGitIntegration(string projectPath)
    {
        try
        {
            // Ensure .spritely/.gitignore
            var spritelyDir = Path.Combine(projectPath, FeatureConstants.SpritelyDir);
            Directory.CreateDirectory(spritelyDir);

            var gitignorePath = Path.Combine(spritelyDir, ".gitignore");
            const string gitignoreContent = """
                # Track features/ (shared context), ignore local caches
                cache/
                *.local.json
                """;

            // Normalize indentation from raw string literal
            var normalizedContent = string.Join('\n',
                gitignoreContent.Split('\n').Select(l => l.TrimStart())) + "\n";

            File.WriteAllText(gitignorePath, normalizedContent, Encoding.UTF8);

            // Check .gitattributes in project root
            var gitattributesPath = Path.Combine(projectPath, ".gitattributes");
            const string spritelyAttributes =
                ".spritely/features/_index.json merge=union text eol=lf\n" +
                ".spritely/features/*.json text eol=lf\n";

            if (File.Exists(gitattributesPath))
            {
                var existing = File.ReadAllText(gitattributesPath, Encoding.UTF8);
                if (!existing.Contains(".spritely/features/"))
                {
                    var suffix = existing.EndsWith('\n') ? "" : "\n";
                    File.AppendAllText(gitattributesPath, suffix + spritelyAttributes, Encoding.UTF8);
                }
            }
            // If .gitattributes doesn't exist, don't create it — only append if it's already there
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Error setting up git integration: {ex.Message}", ex);
        }
    }

    // ── Helper: Report Progress ─────────────────────────────────────────

    private void ReportProgress(string message)
    {
        AppLogger.Debug("FeatureInitializer", message);
        ProgressChanged?.Invoke(message);
    }
}
