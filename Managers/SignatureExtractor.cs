using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Spritely.Constants;

namespace Spritely.Managers;

/// <summary>
/// Local code signature extractor — no LLM calls. Parses source files using regex
/// to extract public API signatures for the Feature System. Output is compact,
/// human-readable plain text designed to be token-efficient when injected into prompts.
/// </summary>
public static class SignatureExtractor
{
    private static readonly HashSet<string> SupportedExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".gd"];

    // ── C# patterns ──────────────────────────────────────────────────────

    private static readonly Regex CsTypeDecl = new(
        @"(?:public|internal)\s+(?:(?:abstract|static|sealed|partial)\s+)*(?:class|interface|enum|struct|record)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex CsPublicMethod = new(
        @"^\s*public\s+(?:(?:static|virtual|override|abstract|async|new|sealed)\s+)*([\w<>\[\]?,\s]+?)\s+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex CsPublicProperty = new(
        @"^\s*public\s+(?:(?:static|virtual|override|abstract|new|required)\s+)*([\w<>\[\]?,\s]+?)\s+(\w+)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex CsEnumValue = new(
        @"^\s*(\w+)\s*[,=]",
        RegexOptions.Compiled);

    // ── TypeScript / JavaScript patterns ─────────────────────────────────

    private static readonly Regex TsExportDecl = new(
        @"export\s+(?:default\s+)?(?:class|function|interface|type|enum|const)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex TsMethodSignature = new(
        @"^\s+(?:async\s+)?(\w+)\s*\(([^)]*)\)(?:\s*:\s*(.+?))?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex TsInterfaceMember = new(
        @"^\s+(?:readonly\s+)?(\w+)(?:\??)\s*:\s*(.+?)\s*;",
        RegexOptions.Compiled);

    // ── Python patterns ──────────────────────────────────────────────────

    private static readonly Regex PyClassDecl = new(
        @"^class\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex PyFuncDecl = new(
        @"^(\s*)def\s+(\w+)\s*\(([^)]*)\)(?:\s*->\s*(.+?))?\s*:",
        RegexOptions.Compiled);

    // ── GDScript patterns ────────────────────────────────────────────────

    private static readonly Regex GdClassName = new(
        @"^class_name\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex GdFunc = new(
        @"^func\s+(\w+)\s*\(([^)]*)\)(?:\s*->\s*(\w+))?",
        RegexOptions.Compiled);

    private static readonly Regex GdSignal = new(
        @"^signal\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex GdExportVar = new(
        @"^@?export\s+var\s+(\w+)(?:\s*:\s*(\w+))?",
        RegexOptions.Compiled);

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads a source file and returns a compact signature block.
    /// Returns an empty string for unsupported, missing, or binary files.
    /// </summary>
    public static string ExtractSignatures(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return string.Empty;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
            return string.Empty;

        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            // Encoding error or access issue — skip gracefully
            return string.Empty;
        }

        // Detect binary files: if the first 8KB contains a NUL byte, treat as binary
        var probe = content[..Math.Min(content.Length, 8192)];
        if (probe.Contains('\0'))
            return string.Empty;

        var lines = content.Split('\n');

        return ext switch
        {
            ".cs" => ExtractCSharp(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" => ExtractTypeScript(lines),
            ".py" => ExtractPython(lines),
            ".gd" => ExtractGDScript(lines),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Returns first 12 hex characters of the SHA-256 hash of the file content.
    /// Used for staleness detection in the feature registry.
    /// </summary>
    public static string ComputeFileHash(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return string.Empty;

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexStringLower(hash)[..FeatureConstants.SignatureHashLength];
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the set of file extensions this extractor can handle.
    /// </summary>
    public static HashSet<string> GetSupportedExtensions() => [..SupportedExtensions];

    // ── C# extraction ────────────────────────────────────────────────────

    private static string ExtractCSharp(string[] lines)
    {
        var output = new List<string>();
        var insideEnum = false;
        var enumValues = new List<string>();
        var braceDepth = 0;
        var enumBraceDepth = 0;

        foreach (var rawLine in lines)
        {
            if (output.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            // Track brace depth for enum value capture
            braceDepth += CountChar(line, '{') - CountChar(line, '}');

            if (insideEnum)
            {
                if (braceDepth <= enumBraceDepth)
                {
                    // Enum closed — flush values
                    if (enumValues.Count > 0)
                        output.Add($"    {string.Join(", ", enumValues)}");
                    insideEnum = false;
                    enumValues.Clear();
                    continue;
                }

                var ev = CsEnumValue.Match(line);
                if (ev.Success)
                    enumValues.Add(ev.Groups[1].Value);
                continue;
            }

            // Type declarations (class, interface, enum, struct, record)
            var typeMatch = CsTypeDecl.Match(line);
            if (typeMatch.Success)
            {
                var typeName = typeMatch.Groups[1].Value;
                var keyword = ExtractCsTypeKeyword(line);
                output.Add($"{keyword} {typeName}");

                if (keyword == "enum")
                {
                    insideEnum = true;
                    enumBraceDepth = braceDepth - CountChar(line, '{');
                }
                continue;
            }

            // Public methods
            var methodMatch = CsPublicMethod.Match(line);
            if (methodMatch.Success)
            {
                var returnType = CollapseWhitespace(methodMatch.Groups[1].Value.Trim());
                var name = methodMatch.Groups[2].Value;
                var parameters = CollapseWhitespace(methodMatch.Groups[3].Value.Trim());
                output.Add($"  {name}({parameters}) -> {returnType}");
                continue;
            }

            // Public properties
            var propMatch = CsPublicProperty.Match(line);
            if (propMatch.Success)
            {
                var propType = CollapseWhitespace(propMatch.Groups[1].Value.Trim());
                var propName = propMatch.Groups[2].Value;
                output.Add($"  {propName}: {propType}");
            }
        }

        // Flush trailing enum values if file ends inside an enum
        if (insideEnum && enumValues.Count > 0 && output.Count < FeatureConstants.MaxSignatureLinesPerFile)
            output.Add($"    {string.Join(", ", enumValues)}");

        return string.Join('\n', output);
    }

    private static string ExtractCsTypeKeyword(string line)
    {
        // Extract the actual keyword (class, interface, enum, struct, record)
        var match = Regex.Match(line, @"\b(class|interface|enum|struct|record)\b");
        return match.Success ? match.Groups[1].Value : "class";
    }

    // ── TypeScript / JavaScript extraction ───────────────────────────────

    private static string ExtractTypeScript(string[] lines)
    {
        var output = new List<string>();
        string? currentType = null;
        var insideBlock = false;
        var blockBraceDepth = 0;
        var braceDepth = 0;

        foreach (var rawLine in lines)
        {
            if (output.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');
            braceDepth += CountChar(line, '{') - CountChar(line, '}');

            // Exported declarations
            var exportMatch = TsExportDecl.Match(line);
            if (exportMatch.Success)
            {
                currentType = exportMatch.Groups[1].Value;
                var keyword = ExtractTsKeyword(line);
                output.Add($"{keyword} {currentType}");

                insideBlock = line.Contains('{');
                if (insideBlock)
                    blockBraceDepth = braceDepth;
                continue;
            }

            if (!insideBlock || currentType is null)
                continue;

            // Detect end of block
            if (braceDepth < blockBraceDepth)
            {
                insideBlock = false;
                currentType = null;
                continue;
            }

            // Method signatures inside a class or object
            var methodMatch = TsMethodSignature.Match(line);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups[1].Value;
                var parameters = CollapseWhitespace(methodMatch.Groups[2].Value.Trim());
                var returnType = methodMatch.Groups[3].Success
                    ? CollapseWhitespace(methodMatch.Groups[3].Value.Trim())
                    : "void";
                output.Add($"  {name}({parameters}) -> {returnType}");
                continue;
            }

            // Interface member signatures
            var memberMatch = TsInterfaceMember.Match(line);
            if (memberMatch.Success)
            {
                var name = memberMatch.Groups[1].Value;
                var type = CollapseWhitespace(memberMatch.Groups[2].Value.Trim());
                output.Add($"  {name}: {type}");
            }
        }

        return string.Join('\n', output);
    }

    private static string ExtractTsKeyword(string line)
    {
        var match = Regex.Match(line, @"\b(class|function|interface|type|enum|const)\b");
        return match.Success ? match.Groups[1].Value : "const";
    }

    // ── Python extraction ────────────────────────────────────────────────

    private static string ExtractPython(string[] lines)
    {
        var output = new List<string>();

        foreach (var rawLine in lines)
        {
            if (output.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            // Class declarations (top-level)
            var classMatch = PyClassDecl.Match(line);
            if (classMatch.Success)
            {
                output.Add($"class {classMatch.Groups[1].Value}");
                continue;
            }

            // Function/method declarations
            var funcMatch = PyFuncDecl.Match(line);
            if (funcMatch.Success)
            {
                var indent = funcMatch.Groups[1].Value;
                var name = funcMatch.Groups[2].Value;
                var parameters = CollapseWhitespace(funcMatch.Groups[3].Value.Trim());
                var returnType = funcMatch.Groups[4].Success ? funcMatch.Groups[4].Value.Trim() : "";

                // Skip private/dunder methods except __init__
                if (name.StartsWith('_') && name != "__init__")
                    continue;

                var prefix = indent.Length > 0 ? "  " : "";
                var arrow = returnType.Length > 0 ? $" -> {returnType}" : "";
                output.Add($"{prefix}{name}({parameters}){arrow}");
            }
        }

        return string.Join('\n', output);
    }

    // ── GDScript extraction ──────────────────────────────────────────────

    private static string ExtractGDScript(string[] lines)
    {
        var output = new List<string>();

        foreach (var rawLine in lines)
        {
            if (output.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            // class_name
            var classMatch = GdClassName.Match(line);
            if (classMatch.Success)
            {
                output.Add($"class {classMatch.Groups[1].Value}");
                continue;
            }

            // func
            var funcMatch = GdFunc.Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                var parameters = CollapseWhitespace(funcMatch.Groups[2].Value.Trim());
                var returnType = funcMatch.Groups[3].Success ? funcMatch.Groups[3].Value : "";

                // Skip private functions (prefixed with _) except _ready, _process, _init
                if (name.StartsWith('_') && name is not ("_ready" or "_process" or "_init" or "_physics_process" or "_enter_tree" or "_exit_tree"))
                    continue;

                var arrow = returnType.Length > 0 ? $" -> {returnType}" : "";
                output.Add($"  {name}({parameters}){arrow}");
                continue;
            }

            // signal
            var signalMatch = GdSignal.Match(line);
            if (signalMatch.Success)
            {
                output.Add($"  signal {signalMatch.Groups[1].Value}");
                continue;
            }

            // export var
            var exportMatch = GdExportVar.Match(line);
            if (exportMatch.Success)
            {
                var varName = exportMatch.Groups[1].Value;
                var varType = exportMatch.Groups[2].Success ? exportMatch.Groups[2].Value : "Variant";
                output.Add($"  {varName}: {varType}");
            }
        }

        return string.Join('\n', output);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string CollapseWhitespace(string input)
    {
        return Regex.Replace(input, @"\s+", " ");
    }

    private static int CountChar(string line, char c)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == c) count++;
        }
        return count;
    }
}
