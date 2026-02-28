using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
{
    public class SettingsManager
    {
        private readonly string _settingsFile;
        private int _historyRetentionHours = 24;
        private string? _lastSelectedProject;
        private bool _settingsPanelCollapsed;
        private int _maxConcurrentTasks = 10;

        public int HistoryRetentionHours
        {
            get => _historyRetentionHours;
            set => _historyRetentionHours = value;
        }

        public string? LastSelectedProject
        {
            get => _lastSelectedProject;
            set => _lastSelectedProject = value;
        }

        public bool SettingsPanelCollapsed
        {
            get => _settingsPanelCollapsed;
            set => _settingsPanelCollapsed = value;
        }

        public int MaxConcurrentTasks
        {
            get => _maxConcurrentTasks;
            set => _maxConcurrentTasks = Math.Max(1, value);
        }

        public SettingsManager(string appDataDir)
        {
            _settingsFile = Path.Combine(appDataDir, "settings.json");
        }

        public async Task LoadSettingsAsync()
        {
            try
            {
                if (!File.Exists(_settingsFile)) return;
                var json = await File.ReadAllTextAsync(_settingsFile).ConfigureAwait(false);
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (dict == null) return;

                if (dict.TryGetValue("historyRetentionHours", out var val))
                    _historyRetentionHours = val.GetInt32();
                if (dict.TryGetValue("selectedProject", out var sp))
                    _lastSelectedProject = sp.GetString();
                if (dict.TryGetValue("settingsPanelCollapsed", out var spc))
                    _settingsPanelCollapsed = spc.GetBoolean();
                if (dict.TryGetValue("maxConcurrentTasks", out var mct))
                    _maxConcurrentTasks = Math.Max(1, mct.GetInt32());
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to load settings", ex); }
        }

        public void SaveSettings(string? projectPath)
        {
            try
            {
                var dict = new Dictionary<string, object>
                {
                    ["historyRetentionHours"] = _historyRetentionHours,
                    ["selectedProject"] = projectPath ?? "",
                    ["settingsPanelCollapsed"] = _settingsPanelCollapsed,
                    ["maxConcurrentTasks"] = _maxConcurrentTasks
                };
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_settingsFile, json, "SettingsManager");
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save settings", ex); }
        }
    }
}
