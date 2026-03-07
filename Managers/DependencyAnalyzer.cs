using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Static code-level dependency analyzer. Scans source files for type definitions
    /// and cross-references to build structured feature-to-feature dependency edges.
    /// No LLM calls — purely deterministic regex-based analysis.
    /// </summary>
    public static class DependencyAnalyzer
    {
        // C# using statements
        private static readonly Regex CsUsingRegex = new(
            @"^\s*using\s+(?:static\s+)?([A-Za-z][\w.]*)\s*;",
            RegexOptions.Compiled);

        // C# type declarations (class, interface, struct, enum, record)
        private static readonly Regex CsTypeDefRegex = new(
            @"(?:public|internal|private|protected)\s+(?:(?:abstract|static|sealed|partial)\s+)*(?:class|interface|enum|struct|record)\s+(\w+)",
            RegexOptions.Compiled);

        // C# base type / interface references in declarations
        private static readonly Regex CsInheritanceRegex = new(
            @"(?:class|interface|struct|record)\s+\w+(?:<[^>]+>)?\s*:\s*([\w\s,<>.]+?)(?:\s*where\b|\s*\{)",
            RegexOptions.Compiled);

        // C# type references in fields, parameters, return types, and generics
        private static readonly Regex CsTypeRefRegex = new(
            @"\b([A-Z]\w{2,})(?:<|\.|\s)",
            RegexOptions.Compiled);

        // TS/JS import statements
        private static readonly Regex TsImportRegex = new(
            @"import\s+.*?\s+from\s+['""]([^'""]+)['""]",
            RegexOptions.Compiled);

        // Python import statements
        private static readonly Regex PyImportRegex = new(
            @"^(?:from\s+([\w.]+)\s+import|import\s+([\w.]+))",
            RegexOptions.Compiled);

        /// <summary>
        /// Analyzes all features' primary files to detect cross-feature type references
        /// and populates <see cref="FeatureEntry.DependsOn"/> accordingly.
        /// </summary>
        public static void AnalyzeDependencies(List<FeatureEntry> features, string projectPath)
        {
            // Phase 1: Build a map of typeName → featureId for all types defined in primary files
            var typeToFeature = BuildTypeOwnershipMap(features, projectPath);

            // Phase 2: For each feature, find which external types its files reference
            foreach (var feature in features)
            {
                var referencedFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var allFiles = feature.PrimaryFiles
                    .Concat(feature.SecondaryFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var relPath in allFiles)
                {
                    var absPath = Path.Combine(projectPath, relPath);
                    if (!File.Exists(absPath))
                        continue;

                    var referencedTypes = ExtractReferencedTypes(absPath);

                    foreach (var typeName in referencedTypes)
                    {
                        if (typeToFeature.TryGetValue(typeName, out var ownerFeatureId)
                            && ownerFeatureId != feature.Id)
                        {
                            referencedFeatureIds.Add(ownerFeatureId);
                        }
                    }
                }

                // Merge with any existing DependsOn (from LLM suggestions) without duplicates
                foreach (var depId in referencedFeatureIds)
                {
                    if (!feature.DependsOn.Contains(depId, StringComparer.OrdinalIgnoreCase))
                        feature.DependsOn.Add(depId);
                }

                feature.DependsOn.Sort(StringComparer.Ordinal);
            }

            AppLogger.Info("DependencyAnalyzer",
                $"Analyzed {features.Count} features. Type map: {typeToFeature.Count} types.");
        }

        /// <summary>
        /// Extracts type names defined in a source file (class, interface, enum, struct, record).
        /// </summary>
        public static HashSet<string> ExtractDefinedTypes(string filePath)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            if (!File.Exists(filePath))
                return result;

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var lines = File.ReadAllLines(filePath);

                if (ext == ".cs")
                {
                    foreach (var line in lines)
                    {
                        var match = CsTypeDefRegex.Match(line);
                        if (match.Success)
                            result.Add(match.Groups[1].Value);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DependencyAnalyzer", $"Error extracting types from '{filePath}': {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Extracts type names referenced (but not necessarily defined) in a source file.
        /// Includes base types, field types, parameter types, and used types.
        /// </summary>
        public static HashSet<string> ExtractReferencedTypes(string filePath)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            if (!File.Exists(filePath))
                return result;

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var lines = File.ReadAllLines(filePath);

                switch (ext)
                {
                    case ".cs":
                        ExtractCsReferences(lines, result);
                        break;
                    case ".ts" or ".tsx" or ".js" or ".jsx":
                        ExtractTsReferences(lines, result);
                        break;
                    case ".py":
                        ExtractPyReferences(lines, result);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DependencyAnalyzer", $"Error extracting references from '{filePath}': {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Builds a lookup mapping type names to the feature that owns them
        /// (the feature whose primary files define the type).
        /// When multiple features define the same type name, the first one wins.
        /// </summary>
        private static Dictionary<string, string> BuildTypeOwnershipMap(
            List<FeatureEntry> features, string projectPath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var feature in features)
            {
                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = Path.Combine(projectPath, relPath);
                    var types = ExtractDefinedTypes(absPath);

                    foreach (var typeName in types)
                        map.TryAdd(typeName, feature.Id);
                }
            }

            return map;
        }

        private static void ExtractCsReferences(string[] lines, HashSet<string> result)
        {
            // Defined types in this file — we'll exclude self-references
            var localTypes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var defMatch = CsTypeDefRegex.Match(line);
                if (defMatch.Success)
                    localTypes.Add(defMatch.Groups[1].Value);
            }

            foreach (var line in lines)
            {
                // Inheritance references
                var inheritMatch = CsInheritanceRegex.Match(line);
                if (inheritMatch.Success)
                {
                    var bases = inheritMatch.Groups[1].Value;
                    foreach (var part in bases.Split(','))
                    {
                        var trimmed = part.Trim().Split('<')[0].Trim();
                        if (trimmed.Length > 2 && char.IsUpper(trimmed[0]) && !localTypes.Contains(trimmed))
                            result.Add(trimmed);
                    }
                }

                // General type references (PascalCase identifiers)
                foreach (Match refMatch in CsTypeRefRegex.Matches(line))
                {
                    var typeName = refMatch.Groups[1].Value;
                    if (!localTypes.Contains(typeName) && !IsCsKeyword(typeName))
                        result.Add(typeName);
                }
            }
        }

        private static void ExtractTsReferences(string[] lines, HashSet<string> result)
        {
            foreach (var line in lines)
            {
                var importMatch = TsImportRegex.Match(line);
                if (importMatch.Success)
                {
                    var module = importMatch.Groups[1].Value;
                    // Extract the last segment as the "type" reference
                    var segments = module.Split('/');
                    var last = segments[^1];
                    if (last.Length > 2)
                        result.Add(last);
                }
            }
        }

        private static void ExtractPyReferences(string[] lines, HashSet<string> result)
        {
            foreach (var line in lines)
            {
                var importMatch = PyImportRegex.Match(line);
                if (importMatch.Success)
                {
                    var module = importMatch.Groups[1].Success
                        ? importMatch.Groups[1].Value
                        : importMatch.Groups[2].Value;

                    var segments = module.Split('.');
                    var last = segments[^1];
                    if (last.Length > 2)
                        result.Add(last);
                }
            }
        }

        private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
        {
            "String", "Boolean", "Int32", "Int64", "Double", "Single", "Decimal",
            "Object", "Void", "Byte", "Char", "DateTime", "TimeSpan", "Guid",
            "Task", "List", "Dictionary", "HashSet", "IEnumerable", "IList",
            "Action", "Func", "EventHandler", "Exception", "StringBuilder",
            "Console", "Math", "Convert", "Encoding", "Path", "File", "Directory",
            "Regex", "CancellationToken", "CancellationTokenSource", "Process",
            "JsonSerializer", "JsonDocument", "JsonElement"
        };

        private static bool IsCsKeyword(string name) => CsKeywords.Contains(name);
    }
}
