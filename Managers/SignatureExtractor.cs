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
/// Kind of symbol extracted from source code.
/// </summary>
public enum SymbolKind
{
    Class,
    Interface,
    Enum,
    Struct,
    Record,
    Method,
    Property,
    Signal,
    ExportVar,
    Function,
    Const,
    Type
}

/// <summary>
/// A structured symbol extracted from a source file.
/// </summary>
public class ExtractedSymbol
{
    public SymbolKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
}

/// <summary>
/// Local code signature extractor — no LLM calls. Parses source files using regex
/// to extract public API signatures for the Feature System. Output is compact,
/// human-readable plain text designed to be token-efficient when injected into prompts.
/// </summary>
public static class SignatureExtractor
{
    private static readonly HashSet<string> SupportedExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".gd", ".xaml"];

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

    // ── XAML patterns ─────────────────────────────────────────────────────

    private static readonly Regex XamlClass = new(
        @"x:Class=""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlNamedElement = new(
        @"x:Name=""(\w+)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlRootElement = new(
        @"<(\w+:)?(\w+)\s",
        RegexOptions.Compiled);

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads a source file and returns a compact signature block.
    /// Returns an empty string for unsupported, missing, or binary files.
    /// </summary>
    public static string ExtractSignatures(string filePath)
    {
        var (lines, ext) = ReadFileLines(filePath);
        if (lines is null) return string.Empty;

        return ext switch
        {
            ".cs" => ExtractCSharp(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" => ExtractTypeScript(lines),
            ".py" => ExtractPython(lines),
            ".gd" => ExtractGDScript(lines),
            ".xaml" => ExtractXaml(lines),
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

    /// <summary>
    /// Extracts structured symbols from a source file. Returns the same symbols
    /// as <see cref="ExtractSignatures"/> but as typed <see cref="ExtractedSymbol"/> objects.
    /// </summary>
    public static List<ExtractedSymbol> ExtractStructuredSymbols(string filePath)
    {
        var (lines, ext) = ReadFileLines(filePath);
        if (lines is null) return [];

        return ext switch
        {
            ".cs" => ExtractCSharpSymbols(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" => ExtractTypeScriptSymbols(lines),
            ".py" => ExtractPythonSymbols(lines),
            ".gd" => ExtractGDScriptSymbols(lines),
            ".xaml" => ExtractXamlSymbols(lines),
            _ => []
        };
    }

    /// <summary>
    /// Returns just the symbol names from a source file.
    /// Useful for populating <c>FeatureEntry.SymbolNames</c>.
    /// </summary>
    public static List<string> GetSymbolNames(string filePath)
    {
        return ExtractStructuredSymbols(filePath)
            .Select(s => s.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Extracts import/using/dependency references from a source file.
    /// Returns namespace or module paths without the language keyword.
    /// </summary>
    public static List<string> ExtractImports(string filePath)
    {
        var (lines, ext) = ReadFileLines(filePath);
        if (lines is null) return [];

        return ext switch
        {
            ".cs" => ExtractCSharpImports(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" => ExtractTypeScriptImports(lines),
            ".py" => ExtractPythonImports(lines),
            ".gd" => ExtractGDScriptImports(lines),
            _ => []
        };
    }

    /// <summary>
    /// Reads and validates a source file, returning its lines and extension.
    /// Returns (null, "") for unsupported, missing, or binary files.
    /// </summary>
    private static (string[]? Lines, string Extension) ReadFileLines(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return (null, "");

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
            return (null, "");

        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            return (null, "");
        }

        var probe = content[..Math.Min(content.Length, 8192)];
        if (probe.Contains('\0'))
            return (null, "");

        return (content.Split('\n'), ext);
    }

    // ── Import patterns ────────────────────────────────────────────────────

    private static readonly Regex CsUsing = new(
        @"^\s*using\s+(?:static\s+)?([A-Za-z][\w.]*)\s*;",
        RegexOptions.Compiled);

    // Matches "using Foo.Bar;" even when split across lines in a global using block
    private static readonly Regex CsGlobalUsing = new(
        @"^\s*global\s+using\s+(?:static\s+)?([A-Za-z][\w.]*)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex TsImport = new(
        @"(?:import|export)\s+.*?from\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex TsImportSideEffect = new(
        @"^\s*import\s+['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex PyImport = new(
        @"^\s*import\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex PyFromImport = new(
        @"^\s*from\s+([\w.]+)\s+import",
        RegexOptions.Compiled);

    private static readonly Regex GdExtends = new(
        @"^\s*extends\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex GdLoad = new(
        @"(?:load|preload)\s*\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled);

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

    // ── XAML extraction ───────────────────────────────────────────────────

    private static string ExtractXaml(string[] lines)
    {
        var output = new List<string>();
        string? rootType = null;

        foreach (var rawLine in lines)
        {
            if (output.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            // x:Class attribute — the code-behind class
            var classMatch = XamlClass.Match(line);
            if (classMatch.Success)
            {
                var fullClass = classMatch.Groups[1].Value;
                var shortName = fullClass.Contains('.') ? fullClass[(fullClass.LastIndexOf('.') + 1)..] : fullClass;
                output.Add($"class {shortName}");
                continue;
            }

            // Root element type (first element in the file)
            if (rootType is null)
            {
                var rootMatch = XamlRootElement.Match(line);
                if (rootMatch.Success)
                {
                    rootType = rootMatch.Groups[2].Value;
                    output.Add($"  root: {rootType}");
                    continue;
                }
            }

            // Named elements
            var namedMatch = XamlNamedElement.Match(line);
            if (namedMatch.Success)
            {
                var name = namedMatch.Groups[1].Value;
                // Try to extract the element type from the same line
                var elemMatch = Regex.Match(line.TrimStart(), @"^<(\w+:)?(\w+)\s");
                var elemType = elemMatch.Success ? elemMatch.Groups[2].Value : "Element";
                output.Add($"  {name}: {elemType}");
            }
        }

        return string.Join('\n', output);
    }

    // ── Structured C# extraction ───────────────────────────────────────

    private static List<ExtractedSymbol> ExtractCSharpSymbols(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();
        var insideEnum = false;
        var braceDepth = 0;
        var enumBraceDepth = 0;

        foreach (var rawLine in lines)
        {
            if (symbols.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');
            braceDepth += CountChar(line, '{') - CountChar(line, '}');

            if (insideEnum)
            {
                if (braceDepth <= enumBraceDepth)
                {
                    insideEnum = false;
                    continue;
                }
                continue;
            }

            var typeMatch = CsTypeDecl.Match(line);
            if (typeMatch.Success)
            {
                var typeName = typeMatch.Groups[1].Value;
                var keyword = ExtractCsTypeKeyword(line);
                var kind = keyword switch
                {
                    "interface" => SymbolKind.Interface,
                    "enum" => SymbolKind.Enum,
                    "struct" => SymbolKind.Struct,
                    "record" => SymbolKind.Record,
                    _ => SymbolKind.Class
                };
                symbols.Add(new ExtractedSymbol { Kind = kind, Name = typeName, Signature = $"{keyword} {typeName}" });

                if (keyword == "enum")
                {
                    insideEnum = true;
                    enumBraceDepth = braceDepth - CountChar(line, '{');
                }
                continue;
            }

            var methodMatch = CsPublicMethod.Match(line);
            if (methodMatch.Success)
            {
                var returnType = CollapseWhitespace(methodMatch.Groups[1].Value.Trim());
                var name = methodMatch.Groups[2].Value;
                var parameters = CollapseWhitespace(methodMatch.Groups[3].Value.Trim());
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Method, Name = name, Signature = $"{name}({parameters}) -> {returnType}" });
                continue;
            }

            var propMatch = CsPublicProperty.Match(line);
            if (propMatch.Success)
            {
                var propType = CollapseWhitespace(propMatch.Groups[1].Value.Trim());
                var propName = propMatch.Groups[2].Value;
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Property, Name = propName, Signature = $"{propName}: {propType}" });
            }
        }

        return symbols;
    }

    // ── Structured TypeScript extraction ─────────────────────────────────

    private static List<ExtractedSymbol> ExtractTypeScriptSymbols(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();
        string? currentType = null;
        var insideBlock = false;
        var blockBraceDepth = 0;
        var braceDepth = 0;

        foreach (var rawLine in lines)
        {
            if (symbols.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');
            braceDepth += CountChar(line, '{') - CountChar(line, '}');

            var exportMatch = TsExportDecl.Match(line);
            if (exportMatch.Success)
            {
                currentType = exportMatch.Groups[1].Value;
                var keyword = ExtractTsKeyword(line);
                var kind = keyword switch
                {
                    "class" => SymbolKind.Class,
                    "interface" => SymbolKind.Interface,
                    "enum" => SymbolKind.Enum,
                    "function" => SymbolKind.Function,
                    "type" => SymbolKind.Type,
                    _ => SymbolKind.Const
                };
                symbols.Add(new ExtractedSymbol { Kind = kind, Name = currentType, Signature = $"{keyword} {currentType}" });

                insideBlock = line.Contains('{');
                if (insideBlock) blockBraceDepth = braceDepth;
                continue;
            }

            if (!insideBlock || currentType is null) continue;

            if (braceDepth < blockBraceDepth)
            {
                insideBlock = false;
                currentType = null;
                continue;
            }

            var methodMatch = TsMethodSignature.Match(line);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups[1].Value;
                var parameters = CollapseWhitespace(methodMatch.Groups[2].Value.Trim());
                var returnType = methodMatch.Groups[3].Success ? CollapseWhitespace(methodMatch.Groups[3].Value.Trim()) : "void";
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Method, Name = name, Signature = $"{name}({parameters}) -> {returnType}" });
                continue;
            }

            var memberMatch = TsInterfaceMember.Match(line);
            if (memberMatch.Success)
            {
                var name = memberMatch.Groups[1].Value;
                var type = CollapseWhitespace(memberMatch.Groups[2].Value.Trim());
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Property, Name = name, Signature = $"{name}: {type}" });
            }
        }

        return symbols;
    }

    // ── Structured Python extraction ─────────────────────────────────────

    private static List<ExtractedSymbol> ExtractPythonSymbols(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();

        foreach (var rawLine in lines)
        {
            if (symbols.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            var classMatch = PyClassDecl.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Class, Name = name, Signature = $"class {name}" });
                continue;
            }

            var funcMatch = PyFuncDecl.Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[2].Value;
                if (name.StartsWith('_') && name != "__init__") continue;

                var parameters = CollapseWhitespace(funcMatch.Groups[3].Value.Trim());
                var returnType = funcMatch.Groups[4].Success ? funcMatch.Groups[4].Value.Trim() : "";
                var arrow = returnType.Length > 0 ? $" -> {returnType}" : "";
                var kind = funcMatch.Groups[1].Value.Length > 0 ? SymbolKind.Method : SymbolKind.Function;
                symbols.Add(new ExtractedSymbol { Kind = kind, Name = name, Signature = $"{name}({parameters}){arrow}" });
            }
        }

        return symbols;
    }

    // ── Structured GDScript extraction ───────────────────────────────────

    private static List<ExtractedSymbol> ExtractGDScriptSymbols(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();

        foreach (var rawLine in lines)
        {
            if (symbols.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            var classMatch = GdClassName.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Class, Name = name, Signature = $"class {name}" });
                continue;
            }

            var funcMatch = GdFunc.Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                if (name.StartsWith('_') && name is not ("_ready" or "_process" or "_init" or "_physics_process" or "_enter_tree" or "_exit_tree"))
                    continue;

                var parameters = CollapseWhitespace(funcMatch.Groups[2].Value.Trim());
                var returnType = funcMatch.Groups[3].Success ? funcMatch.Groups[3].Value : "";
                var arrow = returnType.Length > 0 ? $" -> {returnType}" : "";
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Method, Name = name, Signature = $"{name}({parameters}){arrow}" });
                continue;
            }

            var signalMatch = GdSignal.Match(line);
            if (signalMatch.Success)
            {
                var name = signalMatch.Groups[1].Value;
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Signal, Name = name, Signature = $"signal {name}" });
                continue;
            }

            var exportMatch = GdExportVar.Match(line);
            if (exportMatch.Success)
            {
                var varName = exportMatch.Groups[1].Value;
                var varType = exportMatch.Groups[2].Success ? exportMatch.Groups[2].Value : "Variant";
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.ExportVar, Name = varName, Signature = $"{varName}: {varType}" });
            }
        }

        return symbols;
    }

    // ── Structured XAML extraction ────────────────────────────────────────

    private static List<ExtractedSymbol> ExtractXamlSymbols(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();
        string? rootType = null;

        foreach (var rawLine in lines)
        {
            if (symbols.Count >= FeatureConstants.MaxSignatureLinesPerFile)
                break;

            var line = rawLine.TrimEnd('\r');

            var classMatch = XamlClass.Match(line);
            if (classMatch.Success)
            {
                var fullClass = classMatch.Groups[1].Value;
                var shortName = fullClass.Contains('.') ? fullClass[(fullClass.LastIndexOf('.') + 1)..] : fullClass;
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Class, Name = shortName, Signature = $"class {shortName}" });
                continue;
            }

            if (rootType is null)
            {
                var rootMatch = XamlRootElement.Match(line);
                if (rootMatch.Success)
                {
                    rootType = rootMatch.Groups[2].Value;
                    symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Property, Name = "root", Signature = $"root: {rootType}" });
                    continue;
                }
            }

            var namedMatch = XamlNamedElement.Match(line);
            if (namedMatch.Success)
            {
                var name = namedMatch.Groups[1].Value;
                var elemMatch = Regex.Match(line.TrimStart(), @"^<(\w+:)?(\w+)\s");
                var elemType = elemMatch.Success ? elemMatch.Groups[2].Value : "Element";
                symbols.Add(new ExtractedSymbol { Kind = SymbolKind.Property, Name = name, Signature = $"{name}: {elemType}" });
            }
        }

        return symbols;
    }

    // ── Import extraction ────────────────────────────────────────────────

    private static List<string> ExtractCSharpImports(string[] lines)
    {
        var imports = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Stop scanning after we hit a namespace or type declaration (perf)
            if (CsTypeDecl.IsMatch(line)) break;

            var globalMatch = CsGlobalUsing.Match(line);
            if (globalMatch.Success)
            {
                imports.Add(globalMatch.Groups[1].Value);
                continue;
            }

            var usingMatch = CsUsing.Match(line);
            if (usingMatch.Success)
            {
                // Skip using aliases with '=' (e.g. "using Foo = Bar.Baz;")
                if (line.Contains('=')) continue;
                imports.Add(usingMatch.Groups[1].Value);
            }
        }

        return imports;
    }

    private static List<string> ExtractTypeScriptImports(string[] lines)
    {
        var imports = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var fromMatch = TsImport.Match(line);
            if (fromMatch.Success)
            {
                imports.Add(fromMatch.Groups[1].Value);
                continue;
            }

            var sideEffect = TsImportSideEffect.Match(line);
            if (sideEffect.Success)
                imports.Add(sideEffect.Groups[1].Value);
        }

        return imports;
    }

    private static List<string> ExtractPythonImports(string[] lines)
    {
        var imports = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var fromMatch = PyFromImport.Match(line);
            if (fromMatch.Success)
            {
                imports.Add(fromMatch.Groups[1].Value);
                continue;
            }

            var importMatch = PyImport.Match(line);
            if (importMatch.Success)
                imports.Add(importMatch.Groups[1].Value);
        }

        return imports;
    }

    private static List<string> ExtractGDScriptImports(string[] lines)
    {
        var imports = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var extendsMatch = GdExtends.Match(line);
            if (extendsMatch.Success)
            {
                imports.Add(extendsMatch.Groups[1].Value);
                continue;
            }

            foreach (Match loadMatch in GdLoad.Matches(line))
                imports.Add(loadMatch.Groups[1].Value);
        }

        return imports;
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
