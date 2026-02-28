using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
{
    public enum SuggestionCategory
    {
        General,
        BugFixes,
        NewFeatures,
        Extensions,
        Rules
    }

    public class Suggestion
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public SuggestionCategory Category { get; set; } = SuggestionCategory.General;
    }

    public class HelperManager : IDisposable
    {
        private readonly string _appDataDir;
        private string _currentProjectPath;
        private string _suggestionsFile;
        private string _ignoredFile;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private Func<IEnumerable<string>>? _getActiveTaskDescriptions;
        private readonly HashSet<string> _ignoredTitles = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<Suggestion> Suggestions { get; } = new();
        public bool IsGenerating { get; private set; }

        public event Action? GenerationStarted;
        public event Action? GenerationCompleted;
        public event Action<string>? GenerationFailed;

        public HelperManager(string appDataDir, string projectPath)
        {
            _appDataDir = appDataDir;
            _currentProjectPath = projectPath;
            _suggestionsFile = GetSuggestionsFilePath(projectPath);
            _ignoredFile = GetIgnoredFilePath(projectPath);
            LoadIgnoredTitles();
            LoadSuggestions();
        }

        /// <summary>
        /// Registers a callback that returns descriptions of currently active (non-finished) tasks.
        /// Used to filter out suggestions that are similar to ongoing work.
        /// </summary>
        public void SetActiveTaskSource(Func<IEnumerable<string>> getDescriptions)
        {
            _getActiveTaskDescriptions = getDescriptions;
        }

        public async Task SwitchProjectAsync(string projectPath)
        {
            if (string.Equals(_currentProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
                return;

            // Save current project's suggestions before switching
            SaveSuggestions();

            _currentProjectPath = projectPath;
            _suggestionsFile = GetSuggestionsFilePath(projectPath);
            _ignoredFile = GetIgnoredFilePath(projectPath);

            // Cancel any in-progress generation
            if (IsGenerating)
                CancelGeneration();

            // Load new project's suggestions off the UI thread
            _ignoredTitles.Clear();
            await LoadIgnoredTitlesAsync();
            Suggestions.Clear();
            await LoadSuggestionsAsync();
        }

        private string GetSuggestionsFilePath(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            return Path.Combine(_appDataDir, $"helper_suggestions_{hash}.json");
        }

        private string GetIgnoredFilePath(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            return Path.Combine(_appDataDir, $"helper_ignored_{hash}.json");
        }

        private static string ComputePathHash(string projectPath)
        {
            var normalized = projectPath.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
        }

        public async Task GenerateSuggestionsAsync(string projectPath, SuggestionCategory category, string? guidance = null)
        {
            if (IsGenerating) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            IsGenerating = true;
            Suggestions.Clear();
            SaveSuggestions();
            GenerationStarted?.Invoke();

            Process? process = null;
            try
            {
                var categoryFilter = category switch
                {
                    SuggestionCategory.General => "general improvements (code quality, performance, UX, architecture)",
                    SuggestionCategory.BugFixes => "potential bugs, error handling gaps, edge cases, and reliability issues",
                    SuggestionCategory.NewFeatures => "new features that naturally fit the project's scope and purpose",
                    SuggestionCategory.Extensions => "experimental or ambitious features that extend beyond the current scope — creative ideas that push the project in new directions",
                    SuggestionCategory.Rules => "project-specific rules, constraints, coding conventions, and guidelines that should be enforced when working on this project (e.g. naming conventions, forbidden patterns, required practices, architectural constraints)",
                    _ => "general improvements"
                };

                var prompt =
                    "You are a project analysis agent. Thoroughly explore this project's codebase and suggest improvements.\n\n" +
                    "STEP 1 — EXPLORE:\n" +
                    "- List the top-level directory structure\n" +
                    "- Read key source files, configs, and entry points\n" +
                    "- Understand the architecture, patterns, and current state\n\n" +
                    "STEP 2 — SUGGEST:\n" +
                    $"Focus on: {categoryFilter}\n\n" +
                    "Generate 5-8 actionable suggestions. For each suggestion, provide:\n" +
                    "- A short title starting with an action verb (e.g. \"Add\", \"Refactor\", \"Fix\", \"Implement\")\n" +
                    "- A detailed description (2-4 sentences) written as implementation instructions: specify which files to change, what code to write, and the expected outcome. Do NOT write analytical observations — write step-by-step instructions an engineer can execute immediately.\n\n" +
                    "STEP 3 — OUTPUT:\n" +
                    "Output ONLY a JSON array with objects having \"title\" and \"description\" fields.\n" +
                    "Example:\n" +
                    "[{\"title\": \"Add email validation to login form\", \"description\": \"In LoginForm.cs, add a regex check on the email field in the Submit handler. Return an error message if the format is invalid. Add a unit test in LoginFormTests.cs to verify both valid and invalid emails.\"}]\n\n" +
                    "Output ONLY the JSON array, no other text, no markdown code blocks.";

                if (!string.IsNullOrWhiteSpace(guidance))
                {
                    prompt += $"\n\nADDITIONAL GUIDANCE FROM USER:\n{guidance}";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "-p --max-turns 15 --output-format text",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = projectPath
                };
                psi.Environment.Remove("CLAUDECODE");

                process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                var text = StripAnsi(output).Trim();

                // Try to extract JSON array from the output
                var jsonStart = text.IndexOf('[');
                var jsonEnd = text.LastIndexOf(']');
                if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                {
                    GenerationFailed?.Invoke("Could not parse suggestions from AI response.");
                    return;
                }

                var json = text[jsonStart..(jsonEnd + 1)];
                var items = JsonSerializer.Deserialize<List<SuggestionJson>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (items == null || items.Count == 0)
                {
                    GenerationFailed?.Invoke("AI returned no suggestions.");
                    return;
                }

                foreach (var item in items)
                {
                    var title = item.Title ?? "";
                    if (_ignoredTitles.Contains(title)) continue;

                    Suggestions.Add(new Suggestion
                    {
                        Title = title,
                        Description = item.Description ?? "",
                        Category = category
                    });
                }

                FilterSuggestionsAgainstActiveTasks();
                SaveSuggestions();
                GenerationCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) { AppLogger.Debug("HelperManager", $"Failed to kill process on cancellation: {ex.Message}"); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                GenerationFailed?.Invoke($"Error generating suggestions: {ex.Message}");
            }
            finally
            {
                try { process?.Dispose(); } catch (Exception ex) { AppLogger.Debug("HelperManager", $"Failed to dispose process: {ex.Message}"); }
                IsGenerating = false;
            }
        }

        public void CancelGeneration()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _cts = null;
            IsGenerating = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelGeneration();
        }

        public void ClearSuggestions()
        {
            Suggestions.Clear();
            SaveSuggestions();
        }

        public void RemoveSuggestion(Suggestion suggestion)
        {
            Suggestions.Remove(suggestion);
            SaveSuggestions();
        }

        public void IgnoreSuggestion(Suggestion suggestion)
        {
            if (!string.IsNullOrWhiteSpace(suggestion.Title))
                _ignoredTitles.Add(suggestion.Title);
            Suggestions.Remove(suggestion);
            SaveSuggestions();
            SaveIgnoredTitles();
        }

        public int IgnoredCount => _ignoredTitles.Count;

        public void ClearIgnoredTitles()
        {
            _ignoredTitles.Clear();
            SaveIgnoredTitles();
        }

        /// <summary>
        /// Removes suggestions whose title or description is similar to any active task description.
        /// </summary>
        private void FilterSuggestionsAgainstActiveTasks()
        {
            if (_getActiveTaskDescriptions == null) return;

            var activeDescriptions = _getActiveTaskDescriptions()
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => ExtractKeywords(d))
                .ToList();

            if (activeDescriptions.Count == 0) return;

            var toRemove = Suggestions.Where(s => activeDescriptions.Any(taskKeywords =>
                IsSimilar(ExtractKeywords(s.Title + " " + s.Description), taskKeywords))).ToList();

            foreach (var s in toRemove)
                Suggestions.Remove(s);
        }

        private static HashSet<string> ExtractKeywords(string text)
        {
            var words = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(w => w.Length > 2)
                .ToHashSet();
            return words;
        }

        private static bool IsSimilar(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return false;
            var intersection = a.Intersect(b).Count();
            var smaller = Math.Min(a.Count, b.Count);
            // If more than 50% of the smaller keyword set overlaps, consider them similar
            return intersection >= smaller * 0.5;
        }

        private void SaveSuggestions()
        {
            try
            {
                var entries = Suggestions.Select(s => new SuggestionJson
                {
                    Title = s.Title,
                    Description = s.Description,
                    Category = s.Category.ToString()
                }).ToList();

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_suggestionsFile, json, "HelperManager");
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to save suggestions", ex); }
        }

        private void LoadSuggestions()
        {
            try
            {
                if (!File.Exists(_suggestionsFile)) return;
                var entries = JsonSerializer.Deserialize<List<SuggestionJson>>(
                    File.ReadAllText(_suggestionsFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entries == null) return;

                foreach (var entry in entries)
                {
                    var cat = Enum.TryParse<SuggestionCategory>(entry.Category, out var c)
                        ? c : SuggestionCategory.General;
                    Suggestions.Add(new Suggestion
                    {
                        Title = entry.Title ?? "",
                        Description = entry.Description ?? "",
                        Category = cat
                    });
                }

                FilterSuggestionsAgainstActiveTasks();
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to load suggestions", ex); }
        }

        internal async Task LoadSuggestionsAsync()
        {
            try
            {
                if (!File.Exists(_suggestionsFile)) return;
                var json = await Task.Run(() => File.ReadAllText(_suggestionsFile));
                var entries = JsonSerializer.Deserialize<List<SuggestionJson>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entries == null) return;

                foreach (var entry in entries)
                {
                    var cat = Enum.TryParse<SuggestionCategory>(entry.Category, out var c)
                        ? c : SuggestionCategory.General;
                    Suggestions.Add(new Suggestion
                    {
                        Title = entry.Title ?? "",
                        Description = entry.Description ?? "",
                        Category = cat
                    });
                }

                FilterSuggestionsAgainstActiveTasks();
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to load suggestions async", ex); }
        }

        private void SaveIgnoredTitles()
        {
            try
            {
                var json = JsonSerializer.Serialize(_ignoredTitles.ToList(), new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_ignoredFile, json, "HelperManager");
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to save ignored titles", ex); }
        }

        private void LoadIgnoredTitles()
        {
            try
            {
                if (!File.Exists(_ignoredFile)) return;
                var titles = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_ignoredFile));
                if (titles == null) return;
                foreach (var t in titles)
                    _ignoredTitles.Add(t);
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to load ignored titles", ex); }
        }

        internal async Task LoadIgnoredTitlesAsync()
        {
            try
            {
                if (!File.Exists(_ignoredFile)) return;
                var json = await Task.Run(() => File.ReadAllText(_ignoredFile));
                var titles = JsonSerializer.Deserialize<List<string>>(json);
                if (titles == null) return;
                foreach (var t in titles)
                    _ignoredTitles.Add(t);
            }
            catch (Exception ex) { AppLogger.Warn("HelperManager", "Failed to load ignored titles async", ex); }
        }

        private static string StripAnsi(string text) => Helpers.FormatHelpers.StripAnsiCodes(text);

        private class SuggestionJson
        {
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string? Category { get; set; }
        }
    }
}
