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

        // C# namespace declaration
        private static readonly Regex CsNamespaceRegex = new(
            @"^\s*namespace\s+([\w.]+)",
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
        /// Analyzes import/using statements in each feature's primary files to detect
        /// cross-feature dependencies based on namespace ownership. For each import,
        /// finds which feature owns files in that namespace and adds to DependsOn.
        /// </summary>
        public static void AnalyzeImportDependencies(List<FeatureEntry> features, string projectPath)
        {
            // Build namespace → featureId map from all features' primary files
            var namespaceToFeatures = BuildNamespaceOwnershipMap(features, projectPath);

            foreach (var feature in features)
            {
                var importedFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = Path.Combine(projectPath, relPath);
                    if (!File.Exists(absPath))
                        continue;

                    var imports = SignatureExtractor.ExtractImports(absPath);

                    foreach (var importNs in imports)
                    {
                        // Try exact match first, then progressively shorter prefixes
                        var matchedFeatureIds = ResolveNamespaceToFeatures(importNs, namespaceToFeatures);
                        foreach (var fid in matchedFeatureIds)
                        {
                            if (fid != feature.Id)
                                importedFeatureIds.Add(fid);
                        }
                    }
                }

                foreach (var depId in importedFeatureIds)
                {
                    if (!feature.DependsOn.Contains(depId, StringComparer.OrdinalIgnoreCase))
                        feature.DependsOn.Add(depId);
                }

                feature.DependsOn.Sort(StringComparer.Ordinal);
            }

            AppLogger.Info("DependencyAnalyzer",
                $"Import dependency analysis complete for {features.Count} features.");
        }

        /// <summary>
        /// Computes DependsOn for a single newly-created feature by analyzing its
        /// primary files' imports against the existing feature set.
        /// </summary>
        public static List<string> ComputeDependsOnForNewFeature(
            FeatureEntry newFeature, List<FeatureEntry> existingFeatures, string projectPath)
        {
            var namespaceToFeatures = BuildNamespaceOwnershipMap(existingFeatures, projectPath);
            var typeToFeature = BuildTypeOwnershipMap(existingFeatures, projectPath);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relPath in newFeature.PrimaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                if (!File.Exists(absPath))
                    continue;

                // Import-based dependencies
                var imports = SignatureExtractor.ExtractImports(absPath);
                foreach (var importNs in imports)
                {
                    var matchedFeatureIds = ResolveNamespaceToFeatures(importNs, namespaceToFeatures);
                    foreach (var fid in matchedFeatureIds)
                    {
                        if (fid != newFeature.Id)
                            result.Add(fid);
                    }
                }

                // Type-reference-based dependencies
                var referencedTypes = ExtractReferencedTypes(absPath);
                foreach (var typeName in referencedTypes)
                {
                    if (typeToFeature.TryGetValue(typeName, out var ownerFeatureId)
                        && ownerFeatureId != newFeature.Id)
                    {
                        result.Add(ownerFeatureId);
                    }
                }
            }

            var sorted = result.ToList();
            sorted.Sort(StringComparer.Ordinal);
            return sorted;
        }

        /// <summary>
        /// Infers module membership for features that lack a ParentModuleId.
        /// Checks if the majority of a feature's primary files share a namespace or
        /// directory prefix with features already assigned to a module.
        /// Returns a map of featureId → moduleId for features that should be assigned.
        /// </summary>
        public static Dictionary<string, string> InferModuleMembership(
            List<FeatureEntry> features, List<ModuleEntry> modules)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (modules.Count == 0)
                return result;

            // Build moduleId → set of directory prefixes from assigned features
            var moduleDirPrefixes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var module in modules)
            {
                var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var assignedFeatures = features
                    .Where(f => string.Equals(f.ParentModuleId, module.Id, StringComparison.OrdinalIgnoreCase));

                foreach (var feature in assignedFeatures)
                {
                    foreach (var filePath in feature.PrimaryFiles)
                    {
                        var dir = GetDirectoryPrefix(filePath);
                        if (!string.IsNullOrEmpty(dir))
                            prefixes.Add(dir);
                    }
                }

                if (prefixes.Count > 0)
                    moduleDirPrefixes[module.Id] = prefixes;
            }

            if (moduleDirPrefixes.Count == 0)
                return result;

            // For each unassigned feature, find best matching module by directory overlap
            var unassigned = features.Where(f => string.IsNullOrEmpty(f.ParentModuleId));

            foreach (var feature in unassigned)
            {
                if (feature.PrimaryFiles.Count == 0)
                    continue;

                var featureDirs = feature.PrimaryFiles
                    .Select(GetDirectoryPrefix)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .ToList();

                if (featureDirs.Count == 0)
                    continue;

                // Count how many of the feature's files match each module's directories
                string? bestModuleId = null;
                var bestOverlap = 0;

                foreach (var (moduleId, prefixes) in moduleDirPrefixes)
                {
                    var overlap = featureDirs.Count(dir => dir != null && prefixes.Contains(dir));
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestModuleId = moduleId;
                    }
                }

                // Require majority overlap
                if (bestModuleId != null && bestOverlap > featureDirs.Count / 2)
                    result[feature.Id] = bestModuleId;
            }

            AppLogger.Info("DependencyAnalyzer",
                $"Inferred module membership for {result.Count} features.");
            return result;
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

        /// <summary>
        /// Builds a map from namespace/directory path segments to the features that own
        /// files in those namespaces. Used for import-based dependency resolution.
        /// </summary>
        private static Dictionary<string, HashSet<string>> BuildNamespaceOwnershipMap(
            List<FeatureEntry> features, string projectPath)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var feature in features)
            {
                foreach (var relPath in feature.PrimaryFiles)
                {
                    var absPath = Path.Combine(projectPath, relPath);

                    // Extract declared namespace from C# files
                    var ns = ExtractNamespace(absPath);
                    if (!string.IsNullOrEmpty(ns))
                    {
                        if (!map.TryGetValue(ns, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            map[ns] = set;
                        }
                        set.Add(feature.Id);
                    }

                    // Also map directory path (normalized with dots) for non-C# and fallback
                    var dirKey = ConvertPathToNamespace(relPath);
                    if (!string.IsNullOrEmpty(dirKey))
                    {
                        if (!map.TryGetValue(dirKey, out var dirSet))
                        {
                            dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            map[dirKey] = dirSet;
                        }
                        dirSet.Add(feature.Id);
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Resolves an import namespace to feature IDs by trying exact match,
        /// then progressively shorter namespace prefixes.
        /// </summary>
        private static HashSet<string> ResolveNamespaceToFeatures(
            string importNamespace, Dictionary<string, HashSet<string>> namespaceToFeatures)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Try exact match
            if (namespaceToFeatures.TryGetValue(importNamespace, out var exact))
            {
                result.UnionWith(exact);
                return result;
            }

            // Try progressively shorter prefixes (e.g. Spritely.Managers → Spritely)
            var parts = importNamespace.Split('.');
            for (var i = parts.Length - 1; i >= 1; i--)
            {
                var prefix = string.Join('.', parts[..i]);
                if (namespaceToFeatures.TryGetValue(prefix, out var prefixMatch))
                {
                    result.UnionWith(prefixMatch);
                    return result;
                }
            }

            // Try matching the last segment as a directory name
            var lastSegment = parts[^1];
            foreach (var (key, featureIds) in namespaceToFeatures)
            {
                var keyParts = key.Split('.');
                if (keyParts.Length > 0 &&
                    string.Equals(keyParts[^1], lastSegment, StringComparison.OrdinalIgnoreCase))
                {
                    result.UnionWith(featureIds);
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the namespace declaration from a C# file.
        /// </summary>
        private static string? ExtractNamespace(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".cs")
                return null;

            try
            {
                // Only scan first 50 lines for namespace declaration
                using var reader = new StreamReader(filePath);
                for (var i = 0; i < 50; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;

                    var match = CsNamespaceRegex.Match(line);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
            catch
            {
                // Ignore read errors
            }

            return null;
        }

        /// <summary>
        /// Converts a relative file path to a dot-separated namespace-like key.
        /// E.g. "Managers/TaskExecutionManager.cs" → "Managers".
        /// </summary>
        private static string? ConvertPathToNamespace(string relPath)
        {
            var normalized = relPath.Replace('\\', '/');
            var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || dir == ".")
                return null;

            return dir.Replace('/', '.');
        }

        /// <summary>
        /// Gets the first directory segment from a relative file path.
        /// E.g. "Managers/TaskExecutionManager.cs" → "Managers".
        /// </summary>
        private static string? GetDirectoryPrefix(string relPath)
        {
            var normalized = relPath.Replace('\\', '/');
            var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || dir == ".")
                return null;

            // Return first directory segment for broad grouping
            var firstSlash = dir.IndexOf('/');
            return firstSlash > 0 ? dir[..firstSlash] : dir;
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
