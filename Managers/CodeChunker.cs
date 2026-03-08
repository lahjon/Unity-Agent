using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// A single chunk of code from a source file, with metadata for embedding and retrieval.
    /// </summary>
    public class CodeChunk
    {
        public string FilePath { get; init; } = "";
        public int ChunkIndex { get; init; }
        public int LineStart { get; init; }
        public int LineEnd { get; init; }
        public string Content { get; init; } = "";
        public string? ContainingSymbol { get; init; }
    }

    /// <summary>
    /// Splits source code files into semantically meaningful chunks at class/function
    /// boundaries. Falls back to fixed-size overlapping windows when AST-style splitting
    /// isn't applicable.
    /// </summary>
    public static class CodeChunker
    {
        // Patterns that identify code structure boundaries
        private static readonly Regex CSharpBoundary = new(
            @"^\s*(public|private|protected|internal|static|abstract|sealed|partial|async|override|virtual)?\s*" +
            @"(class|interface|enum|struct|record|namespace)\s+\w+",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex CSharpMethodBoundary = new(
            @"^\s*(public|private|protected|internal|static|async|override|virtual)\s+\S+\s+\w+\s*[<(]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex TypeScriptBoundary = new(
            @"^\s*(export\s+)?(default\s+)?(class|interface|function|enum|type|const|let)\s+\w+",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex PythonBoundary = new(
            @"^(class\s+\w+|def\s+\w+|async\s+def\s+\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex GdScriptBoundary = new(
            @"^(class_name\s+\w+|func\s+\w+|class\s+\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Chunk a source file into semantically meaningful pieces.
        /// Tries to split at class/function boundaries; falls back to fixed-size windows.
        /// </summary>
        public static List<CodeChunk> ChunkFile(string filePath, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new List<CodeChunk>();

            var lines = content.Split('\n');
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            // Try boundary-based splitting
            var boundaryRegex = ext switch
            {
                ".cs" => CSharpBoundary,
                ".ts" or ".tsx" or ".js" or ".jsx" => TypeScriptBoundary,
                ".py" => PythonBoundary,
                ".gd" => GdScriptBoundary,
                _ => null
            };

            var methodRegex = ext == ".cs" ? CSharpMethodBoundary : null;

            var boundaries = FindBoundaries(lines, boundaryRegex, methodRegex);

            if (boundaries.Count > 1)
                return ChunkByBoundaries(filePath, lines, boundaries);

            // Fallback: fixed-size overlapping chunks
            return ChunkFixedSize(filePath, lines);
        }

        /// <summary>
        /// Chunk multiple files, returning all chunks with file path metadata.
        /// </summary>
        public static List<CodeChunk> ChunkFiles(IEnumerable<(string path, string content)> files)
        {
            var allChunks = new List<CodeChunk>();
            foreach (var (path, content) in files)
            {
                var chunks = ChunkFile(path, content);
                allChunks.AddRange(chunks);
            }
            return allChunks;
        }

        private static List<int> FindBoundaries(string[] lines, Regex? typeRegex, Regex? methodRegex)
        {
            var boundaries = new List<int> { 0 }; // always start at line 0

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (typeRegex != null && typeRegex.IsMatch(line))
                {
                    if (i > 0 && i != boundaries.Last())
                        boundaries.Add(i);
                }
                else if (methodRegex != null && methodRegex.IsMatch(line))
                {
                    if (i > 0 && i != boundaries.Last())
                        boundaries.Add(i);
                }
            }

            return boundaries;
        }

        private static List<CodeChunk> ChunkByBoundaries(string filePath, string[] lines, List<int> boundaries)
        {
            var chunks = new List<CodeChunk>();
            var targetChars = EmbeddingConstants.TargetChunkTokens * 4; // ~4 chars per token

            for (int b = 0; b < boundaries.Count && chunks.Count < EmbeddingConstants.MaxChunksPerFile; b++)
            {
                var start = boundaries[b];
                var end = b + 1 < boundaries.Count ? boundaries[b + 1] : lines.Length;

                // Merge small sections with the next boundary
                var sectionContent = string.Join("\n", lines[start..end]);
                if (sectionContent.Length < EmbeddingConstants.MinChunkChars && b + 1 < boundaries.Count)
                    continue;

                // Split oversized sections into sub-chunks
                if (sectionContent.Length > targetChars * 2)
                {
                    var subChunks = SplitOversizedSection(filePath, lines, start, end, targetChars, chunks.Count);
                    chunks.AddRange(subChunks);
                }
                else
                {
                    // Detect containing symbol from first line
                    var symbolName = ExtractSymbolName(lines[start]);

                    chunks.Add(new CodeChunk
                    {
                        FilePath = filePath,
                        ChunkIndex = chunks.Count,
                        LineStart = start + 1,
                        LineEnd = end,
                        Content = sectionContent,
                        ContainingSymbol = symbolName
                    });
                }
            }

            return chunks;
        }

        private static List<CodeChunk> SplitOversizedSection(
            string filePath, string[] lines, int start, int end, int targetChars, int startIndex)
        {
            var chunks = new List<CodeChunk>();
            var overlapLines = Math.Max(1, EmbeddingConstants.ChunkOverlapTokens / 10);
            int pos = start;

            while (pos < end && chunks.Count + startIndex < EmbeddingConstants.MaxChunksPerFile)
            {
                int chunkEnd = pos;
                int charCount = 0;

                while (chunkEnd < end && charCount < targetChars)
                {
                    charCount += lines[chunkEnd].Length + 1;
                    chunkEnd++;
                }

                var content = string.Join("\n", lines[pos..chunkEnd]);
                chunks.Add(new CodeChunk
                {
                    FilePath = filePath,
                    ChunkIndex = startIndex + chunks.Count,
                    LineStart = pos + 1,
                    LineEnd = chunkEnd,
                    Content = content,
                    ContainingSymbol = ExtractSymbolName(lines[pos])
                });

                pos = Math.Max(pos + 1, chunkEnd - overlapLines);
            }

            return chunks;
        }

        private static List<CodeChunk> ChunkFixedSize(string filePath, string[] lines)
        {
            var chunks = new List<CodeChunk>();
            var targetLines = Math.Max(10, EmbeddingConstants.TargetChunkTokens / 5); // ~5 tokens per line
            var overlapLines = Math.Max(1, EmbeddingConstants.ChunkOverlapTokens / 5);

            int pos = 0;
            while (pos < lines.Length && chunks.Count < EmbeddingConstants.MaxChunksPerFile)
            {
                var end = Math.Min(pos + targetLines, lines.Length);
                var content = string.Join("\n", lines[pos..end]);

                if (content.Length >= EmbeddingConstants.MinChunkChars || pos == 0)
                {
                    chunks.Add(new CodeChunk
                    {
                        FilePath = filePath,
                        ChunkIndex = chunks.Count,
                        LineStart = pos + 1,
                        LineEnd = end,
                        Content = content
                    });
                }

                pos = end - overlapLines;
                if (pos <= (chunks.Count > 0 ? chunks.Last().LineEnd - 1 : 0))
                    pos = end; // prevent infinite loop
            }

            return chunks;
        }

        private static string? ExtractSymbolName(string line)
        {
            // Try to extract a class/function/method name from the line
            var match = Regex.Match(line.Trim(),
                @"(?:class|interface|enum|struct|record|function|func|def|type)\s+(\w+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
