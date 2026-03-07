using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spritely.Managers
{
    /// <summary>
    /// Discovers and loads CLAUDE.md files and .claude/rules/ from target projects,
    /// following Anthropic's rules hierarchy:
    ///   1. CLAUDE.md (project root)
    ///   2. .claude/CLAUDE.md (alternative location)
    ///   3. CLAUDE.local.md (personal, not in git)
    ///   4. .claude/rules/*.md (modular rule files, supports subdirectories)
    ///
    /// Path-scoped rules use YAML frontmatter with a "paths:" field — these are
    /// stored but only surfaced when relevant files are being touched.
    /// </summary>
    public class RulesManager
    {
        private readonly ConcurrentDictionary<string, ProjectRulesCache> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the assembled rules block for a project, reading from disk and caching.
        /// Cache is invalidated if any source file changed since last load.
        /// </summary>
        public string GetRulesBlock(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
                return "";

            if (_cache.TryGetValue(projectPath, out var cached) && !cached.IsStale())
                return cached.AssembledBlock;

            var newCache = LoadRules(projectPath);
            _cache[projectPath] = newCache;
            return newCache.AssembledBlock;
        }

        /// <summary>
        /// Forces a cache refresh for a specific project.
        /// </summary>
        public void InvalidateCache(string projectPath)
        {
            _cache.TryRemove(projectPath, out _);
        }

        /// <summary>
        /// Clears all cached rules.
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }

        private static ProjectRulesCache LoadRules(string projectPath)
        {
            var entries = new List<RuleFileEntry>();
            var sb = new StringBuilder();

            // 1. CLAUDE.md at project root
            var claudeMd = Path.Combine(projectPath, "CLAUDE.md");
            TryLoadFile(claudeMd, entries);

            // 2. .claude/CLAUDE.md (alternative location, only if root CLAUDE.md doesn't exist)
            var dotClaudeMd = Path.Combine(projectPath, ".claude", "CLAUDE.md");
            if (!File.Exists(claudeMd))
                TryLoadFile(dotClaudeMd, entries);

            // 3. CLAUDE.local.md (personal overrides, not in git)
            var localMd = Path.Combine(projectPath, "CLAUDE.local.md");
            TryLoadFile(localMd, entries);

            // 4. .claude/rules/*.md (modular rule files, recursive)
            var rulesDir = Path.Combine(projectPath, ".claude", "rules");
            if (Directory.Exists(rulesDir))
            {
                var ruleFiles = Directory.GetFiles(rulesDir, "*.md", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var file in ruleFiles)
                    TryLoadFile(file, entries);
            }

            // Assemble: only include always-on rules (no path scope) in the default block
            foreach (var entry in entries)
            {
                if (entry.PathScopes.Count > 0)
                    continue; // Path-scoped rules are included on demand via GetScopedRules

                if (!string.IsNullOrWhiteSpace(entry.Content))
                {
                    sb.AppendLine(entry.Content.TrimEnd());
                    sb.AppendLine();
                }
            }

            var block = sb.Length > 0
                ? "# CLAUDE.MD RULES\n" + sb
                : "";

            return new ProjectRulesCache
            {
                Entries = entries,
                AssembledBlock = block,
                LoadedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Returns path-scoped rules that match any of the given file paths.
        /// Used when a task touches specific files and we want to include relevant scoped rules.
        /// </summary>
        public string GetScopedRules(string projectPath, IEnumerable<string> touchedFiles)
        {
            if (!_cache.TryGetValue(projectPath, out var cached))
            {
                cached = LoadRules(projectPath);
                _cache[projectPath] = cached;
            }

            var scopedEntries = cached.Entries
                .Where(e => e.PathScopes.Count > 0)
                .Where(e => touchedFiles.Any(f => MatchesAnyScope(f, e.PathScopes, projectPath)))
                .ToList();

            if (scopedEntries.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("# SCOPED RULES");
            foreach (var entry in scopedEntries)
            {
                sb.AppendLine(entry.Content.TrimEnd());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static bool MatchesAnyScope(string filePath, List<string> scopes, string projectRoot)
        {
            // Normalize to relative path
            var relative = filePath;
            if (filePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                relative = filePath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            relative = relative.Replace('\\', '/');

            foreach (var scope in scopes)
            {
                var pattern = scope.Replace('\\', '/');
                if (MatchGlob(relative, pattern))
                    return true;
            }
            return false;
        }

        /// <summary>Simple glob matcher supporting * and ** patterns.</summary>
        private static bool MatchGlob(string path, string pattern)
        {
            // Convert glob to a simple check
            if (pattern == "**" || pattern == "**/*") return true;

            // "src/**/*.ts" → check prefix and extension
            var parts = pattern.Split(new[] { "**" }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var prefix = parts[0].TrimEnd('/');
                var suffix = parts[1].TrimStart('/');

                var prefixMatch = string.IsNullOrEmpty(prefix) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
                var suffixMatch = string.IsNullOrEmpty(suffix) || MatchSimpleGlob(Path.GetFileName(path), suffix);
                return prefixMatch && suffixMatch;
            }

            return MatchSimpleGlob(path, pattern);
        }

        private static bool MatchSimpleGlob(string input, string pattern)
        {
            // Simple * matching (not recursive)
            if (pattern == "*") return true;
            if (pattern.StartsWith("*."))
            {
                var ext = pattern.Substring(1);
                return input.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryLoadFile(string filePath, List<RuleFileEntry> entries)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                var content = File.ReadAllText(filePath);
                var lastWrite = File.GetLastWriteTimeUtc(filePath);
                var (pathScopes, body) = ParseFrontmatter(content);

                entries.Add(new RuleFileEntry
                {
                    FilePath = filePath,
                    Content = body,
                    PathScopes = pathScopes,
                    LastWriteUtc = lastWrite
                });
            }
            catch
            {
                // Silently skip unreadable files
            }
        }

        /// <summary>
        /// Parses optional YAML frontmatter for path scoping.
        /// Format:
        /// ---
        /// paths:
        ///   - "src/api/**/*.ts"
        /// ---
        /// </summary>
        private static (List<string> paths, string body) ParseFrontmatter(string content)
        {
            var paths = new List<string>();

            if (!content.StartsWith("---"))
                return (paths, content);

            var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIndex < 0)
                return (paths, content);

            var frontmatter = content.Substring(3, endIndex - 3);
            var body = content.Substring(endIndex + 3).TrimStart('\r', '\n');

            // Simple YAML parsing for paths: field
            var inPaths = false;
            foreach (var line in frontmatter.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("paths:"))
                {
                    inPaths = true;
                    continue;
                }
                if (inPaths && trimmed.StartsWith("- "))
                {
                    var path = trimmed.Substring(2).Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }
                else if (inPaths && !string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("-"))
                {
                    inPaths = false;
                }
            }

            return (paths, body);
        }

        private class RuleFileEntry
        {
            public string FilePath { get; set; } = "";
            public string Content { get; set; } = "";
            public List<string> PathScopes { get; set; } = new();
            public DateTime LastWriteUtc { get; set; }
        }

        private class ProjectRulesCache
        {
            public List<RuleFileEntry> Entries { get; set; } = new();
            public string AssembledBlock { get; set; } = "";
            public DateTime LoadedAt { get; set; }

            /// <summary>Stale if any source file was modified since cache load.</summary>
            public bool IsStale()
            {
                foreach (var entry in Entries)
                {
                    try
                    {
                        if (!File.Exists(entry.FilePath)) return true;
                        if (File.GetLastWriteTimeUtc(entry.FilePath) > LoadedAt) return true;
                    }
                    catch { return true; }
                }

                // Also stale if new files appeared in .claude/rules/ that weren't tracked
                var firstEntry = Entries.FirstOrDefault();
                if (firstEntry == null) return false;

                var projectPath = GuessProjectPath(firstEntry.FilePath);
                if (projectPath == null) return false;

                var rulesDir = Path.Combine(projectPath, ".claude", "rules");
                if (!Directory.Exists(rulesDir)) return false;

                var currentFiles = Directory.GetFiles(rulesDir, "*.md", SearchOption.AllDirectories);
                var trackedRuleFiles = Entries.Where(e => e.FilePath.StartsWith(rulesDir, StringComparison.OrdinalIgnoreCase)).Select(e => e.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return currentFiles.Any(f => !trackedRuleFiles.Contains(f));
            }

            private static string? GuessProjectPath(string entryPath)
            {
                // Walk up to find project root from a known rule file path
                var dir = Path.GetDirectoryName(entryPath);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir, "CLAUDE.md")) ||
                        Directory.Exists(Path.Combine(dir, ".claude")))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
                return null;
            }
        }
    }
}
