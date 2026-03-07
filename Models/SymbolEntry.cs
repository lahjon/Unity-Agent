using System.Collections.Generic;
using Spritely.Managers;

namespace Spritely.Models
{
    /// <summary>
    /// A single code symbol extracted from a source file by <see cref="Managers.SignatureExtractor"/>.
    /// </summary>
    public class ExtractedSymbol
    {
        /// <summary>What kind of symbol this is (class, method, property, etc.).</summary>
        public SymbolKind Kind { get; set; }

        /// <summary>The symbol's short name (e.g. "TaskExecutionManager").</summary>
        public string Name { get; set; } = "";

        /// <summary>The full signature line as extracted from source (e.g. "public class TaskExecutionManager : IDisposable").</summary>
        public string Signature { get; set; } = "";

        /// <summary>Relative path to the file this symbol was extracted from.</summary>
        public string FilePath { get; set; } = "";
    }

    /// <summary>
    /// Project-wide symbol index stored as <c>_codebase_index.json</c>.
    /// Maps lowercased symbol names to their location and feature membership.
    /// </summary>
    public class CodebaseSymbolIndex
    {
        /// <summary>Schema version — bump when the format changes.</summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// All symbols in the project, keyed by lowercased symbol name.
        /// When multiple symbols share a name, the value reflects the most relevant one.
        /// </summary>
        public Dictionary<string, SymbolIndexEntry> Symbols { get; set; } = new();
    }

    /// <summary>
    /// A single entry in the <see cref="CodebaseSymbolIndex"/> — enough to locate a symbol
    /// and link it to its owning feature without loading full feature files.
    /// </summary>
    public class SymbolIndexEntry
    {
        /// <summary>The feature that owns this symbol, if any.</summary>
        public string FeatureId { get; set; } = "";

        /// <summary>Relative path to the file containing this symbol.</summary>
        public string FilePath { get; set; } = "";

        /// <summary>The kind of symbol (Class, Method, etc.) as a string.</summary>
        public string Kind { get; set; } = "";

        /// <summary>A compact one-line signature for quick display.</summary>
        public string ShortSignature { get; set; } = "";
    }

    /// <summary>
    /// Result of searching the <see cref="CodebaseSymbolIndex"/> for a symbol name.
    /// </summary>
    public class SymbolSearchResult
    {
        /// <summary>The matched symbol name (original casing).</summary>
        public string SymbolName { get; set; } = "";

        /// <summary>The feature this symbol belongs to.</summary>
        public string FeatureId { get; set; } = "";

        /// <summary>Match confidence from 0.0 (no match) to 1.0 (exact match).</summary>
        public double Confidence { get; set; }
    }
}
