using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public class SettingsManager
    {
        private readonly string _settingsFile;
        private readonly string _templatesFile;
        private int _historyRetentionHours = 24;
        private string? _lastSelectedProject;
        private bool _settingsPanelCollapsed;
        private int _maxConcurrentTasks = 10;
        private int _tokenLimitRetryMinutes = 30;

        public List<TaskTemplate> TaskTemplates { get; } = new();

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

        public int TokenLimitRetryMinutes
        {
            get => _tokenLimitRetryMinutes;
            set => _tokenLimitRetryMinutes = Math.Max(1, value);
        }

        public SettingsManager(string appDataDir)
        {
            _settingsFile = Path.Combine(appDataDir, "settings.json");
            _templatesFile = Path.Combine(appDataDir, "task_templates.json");
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
                if (dict.TryGetValue("tokenLimitRetryMinutes", out var tlr))
                    _tokenLimitRetryMinutes = Math.Max(1, tlr.GetInt32());
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
                    ["maxConcurrentTasks"] = _maxConcurrentTasks,
                    ["tokenLimitRetryMinutes"] = _tokenLimitRetryMinutes
                };
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_settingsFile, json, "SettingsManager");
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save settings", ex); }
        }

        public async Task LoadTemplatesAsync()
        {
            try
            {
                if (!File.Exists(_templatesFile)) return;
                var json = await File.ReadAllTextAsync(_templatesFile).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<List<TaskTemplate>>(json);
                if (entries != null)
                {
                    TaskTemplates.Clear();
                    TaskTemplates.AddRange(entries);
                }
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to load task templates", ex); }
        }

        public void SaveTemplates()
        {
            try
            {
                var json = JsonSerializer.Serialize(TaskTemplates,
                    new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_templatesFile, json, "SettingsManager");
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save task templates", ex); }
        }
    }
}
