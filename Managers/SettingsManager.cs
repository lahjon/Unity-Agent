using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UnityAgent.Managers
{
    public class SettingsManager
    {
        private readonly string _settingsFile;
        private int _historyRetentionHours = 24;
        private string? _lastSelectedProject;
        private bool _settingsPanelCollapsed;

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

        public SettingsManager(string appDataDir)
        {
            _settingsFile = Path.Combine(appDataDir, "settings.json");
        }

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFile)) return;
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(_settingsFile));
                if (dict == null) return;

                if (dict.TryGetValue("historyRetentionHours", out var val))
                    _historyRetentionHours = val.GetInt32();
                if (dict.TryGetValue("selectedProject", out var sp))
                    _lastSelectedProject = sp.GetString();
                if (dict.TryGetValue("settingsPanelCollapsed", out var spc))
                    _settingsPanelCollapsed = spc.GetBoolean();
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
                    ["settingsPanelCollapsed"] = _settingsPanelCollapsed
                };
                File.WriteAllText(_settingsFile,
                    JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save settings", ex); }
        }
    }
}
