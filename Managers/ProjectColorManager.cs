using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages project color assignment and lookup.
    /// </summary>
    public class ProjectColorManager
    {
        private static readonly Random _rng = new();

        private static readonly string[] ProjectColorPalette =
        {
            "#D4806B", // soft coral
            "#6BA3A0", // soft teal
            "#9B8EC4", // soft lavender
            "#C4A94D", // soft gold
            "#7BAF7B", // soft sage
            "#6B8FD4", // soft blue
            "#C47B8E", // soft rose
            "#D4A06B", // soft amber
            "#6BC4A0", // soft mint
            "#B08EB0", // soft mauve
            "#8BAFC4", // soft steel
            "#C49B6B", // soft tan
        };

        private readonly IProjectDataProvider _data;

        public ProjectColorManager(IProjectDataProvider data)
        {
            _data = data;
        }

        public string PickProjectColor()
        {
            var used = new HashSet<string>(
                _data.SavedProjects
                    .Where(p => !string.IsNullOrEmpty(p.Color))
                    .Select(p => p.Color),
                StringComparer.OrdinalIgnoreCase);
            var available = ProjectColorPalette.Where(c => !used.Contains(c)).ToArray();
            if (available.Length > 0)
                return available[_rng.Next(available.Length)];

            for (var i = 0; i < 200; i++)
            {
                var candidate = $"#{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}";
                if (!used.Contains(candidate))
                    return candidate;
            }
            return $"#{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}";
        }

        public string GetProjectColor(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.Color ?? "#666666";
        }

        public string GetProjectDisplayName(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.DisplayName ?? Path.GetFileName(projectPath);
        }

        /// <summary>
        /// Backfills colors for projects that don't have one yet. Returns true if any were changed.
        /// </summary>
        public bool BackfillColors()
        {
            var changed = false;
            foreach (var p in _data.SavedProjects.Where(p => string.IsNullOrEmpty(p.Color)))
            {
                p.Color = PickProjectColor();
                changed = true;
            }
            return changed;
        }
    }
}
