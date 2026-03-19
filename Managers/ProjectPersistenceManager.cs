using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Spritely.Managers.DataStore;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Handles project list serialization, file I/O, and load/save operations.
    /// </summary>
    public class ProjectPersistenceManager
    {
        private readonly string _projectsFile;
        private readonly IDataStore<List<ProjectEntry>> _projectStore;

        private static readonly JsonSerializerOptions _projectJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public ProjectPersistenceManager(string appDataDir)
        {
            _projectsFile = Path.Combine(appDataDir, "projects.json");
            _projectStore = new JsonDataStore<List<ProjectEntry>>(_projectsFile, new DataStoreOptions
            {
                SchemaVersion = 1,
                BackgroundWrite = true,
                CallerName = "ProjectManager",
                JsonOptions = _projectJsonOptions
            });
        }

        public async Task<List<ProjectEntry>> LoadAsync()
        {
            try
            {
                var loaded = await _projectStore.LoadAsync().ConfigureAwait(false);
                if (loaded != null)
                    return loaded;

                if (File.Exists(_projectsFile))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(_projectsFile).ConfigureAwait(false);
                        var oldList = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                        return oldList.Select(p => new ProjectEntry { Path = p }).ToList();
                    }
                    catch { return new(); }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", "Failed to load projects", ex);
            }

            return new();
        }

        public void Save(List<ProjectEntry> projects)
        {
            try
            {
                _ = _projectStore.SaveAsync(projects);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", "Failed to save projects", ex);
            }
        }
    }
}
