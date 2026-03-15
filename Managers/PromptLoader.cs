using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace Spritely.Managers
{
    /// <summary>
    /// Loads prompt templates from embedded resource .md files under the Prompts/ folder.
    /// Each resource is read exactly once and cached in memory for all subsequent accesses.
    /// </summary>
    internal static class PromptLoader
    {
        private static readonly Assembly _assembly = typeof(PromptLoader).Assembly;
        private static readonly ConcurrentDictionary<string, string> _cache = new();
        private const string Prefix = "Spritely.Prompts.";

        /// <summary>
        /// Returns the contents of an embedded .md resource by path relative to Prompts/.
        /// Accepts both flat names ("Foo.md") and subdirectory paths ("Core/Foo.md").
        /// The result is cached on first read — subsequent calls return the cached string.
        /// </summary>
        public static string Load(string filename)
        {
            return _cache.GetOrAdd(filename, static (key, asm) =>
            {
                var resourceName = Prefix + key.Replace('/', '.').Replace('\\', '.');
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                    throw new InvalidOperationException(
                        $"Embedded prompt resource '{resourceName}' not found. " +
                        $"Ensure the file Prompts/{key} exists and is marked as EmbeddedResource.");

                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                // Normalize to \n line endings for consistent prompt formatting
                text = text.Replace("\r\n", "\n").TrimEnd('\n') + "\n\n";
                return text;
            }, _assembly);
        }
    }
}


