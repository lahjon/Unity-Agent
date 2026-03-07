using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Models;

namespace Spritely.Managers
{
    public class SettingsManager
    {
        private readonly string _settingsFile;
        private string? _templatesDir;
        private readonly string _globalTemplatesFile;
        private int _historyRetentionHours = 24;
        private string? _lastSelectedProject;
        private bool _settingsPanelCollapsed;
        private bool _leftPanelCollapsed;
        private int _maxConcurrentTasks = 10;
        private int _tokenLimitRetryMinutes = 30;
        private int _taskTimeoutMinutes = 120;
        private bool _autoVerify;
        private bool _autoCommit;
        private string _defaultMcpServerName = "mcp-for-unity-server";
        private string _defaultMcpAddress = "http://127.0.0.1:8080/mcp";
        private string _defaultMcpStartCommand = @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools";
        private string _opusEffortLevel = "high";

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

        public bool LeftPanelCollapsed
        {
            get => _leftPanelCollapsed;
            set => _leftPanelCollapsed = value;
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

        public int TaskTimeoutMinutes
        {
            get => _taskTimeoutMinutes;
            set => _taskTimeoutMinutes = Math.Max(1, value);
        }

        public bool AutoVerify
        {
            get => _autoVerify;
            set => _autoVerify = value;
        }

        public bool AutoCommit
        {
            get => _autoCommit;
            set => _autoCommit = value;
        }

        public string DefaultMcpServerName
        {
            get => _defaultMcpServerName;
            set => _defaultMcpServerName = value ?? "mcp-for-unity-server";
        }

        public string DefaultMcpAddress
        {
            get => _defaultMcpAddress;
            set => _defaultMcpAddress = value ?? "http://127.0.0.1:8080/mcp";
        }

        public string DefaultMcpStartCommand
        {
            get => _defaultMcpStartCommand;
            set => _defaultMcpStartCommand = value ?? @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools";
        }

        public string OpusEffortLevel
        {
            get => _opusEffortLevel;
            set => _opusEffortLevel = value is "low" or "medium" or "high" ? value : "high";
        }

        public SettingsManager(string appDataDir)
        {
            _settingsFile = Path.Combine(appDataDir, "settings.json");
            _globalTemplatesFile = Path.Combine(appDataDir, "task_templates.json");
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
                if (dict.TryGetValue("leftPanelCollapsed", out var lpc))
                    _leftPanelCollapsed = lpc.GetBoolean();
                if (dict.TryGetValue("maxConcurrentTasks", out var mct))
                    _maxConcurrentTasks = Math.Max(1, mct.GetInt32());
                if (dict.TryGetValue("tokenLimitRetryMinutes", out var tlr))
                    _tokenLimitRetryMinutes = Math.Max(1, tlr.GetInt32());
                if (dict.TryGetValue("taskTimeoutMinutes", out var ttm))
                    _taskTimeoutMinutes = Math.Max(1, ttm.GetInt32());
                if (dict.TryGetValue("autoVerify", out var av))
                    _autoVerify = av.GetBoolean();
                if (dict.TryGetValue("autoCommit", out var ac))
                    _autoCommit = ac.GetBoolean();
                if (dict.TryGetValue("defaultMcpServerName", out var dmsn))
                    _defaultMcpServerName = dmsn.GetString() ?? "mcp-for-unity-server";
                if (dict.TryGetValue("defaultMcpAddress", out var dma))
                    _defaultMcpAddress = dma.GetString() ?? "http://127.0.0.1:8080/mcp";
                if (dict.TryGetValue("defaultMcpStartCommand", out var dmsc))
                    _defaultMcpStartCommand = dmsc.GetString() ?? @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools";
                if (dict.TryGetValue("opusEffortLevel", out var oel))
                    OpusEffortLevel = oel.GetString() ?? "high";
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
                    ["leftPanelCollapsed"] = _leftPanelCollapsed,
                    ["maxConcurrentTasks"] = _maxConcurrentTasks,
                    ["tokenLimitRetryMinutes"] = _tokenLimitRetryMinutes,
                    ["taskTimeoutMinutes"] = _taskTimeoutMinutes,
                    ["autoVerify"] = _autoVerify,
                    ["autoCommit"] = _autoCommit,
                    ["defaultMcpServerName"] = _defaultMcpServerName,
                    ["defaultMcpAddress"] = _defaultMcpAddress,
                    ["defaultMcpStartCommand"] = _defaultMcpStartCommand,
                    ["opusEffortLevel"] = _opusEffortLevel
                };
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_settingsFile, json, "SettingsManager");
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save settings", ex); }
        }

        public async Task LoadTemplatesAsync(string? projectPath)
        {
            TaskTemplates.Clear();
            _templatesDir = ResolveTemplatesDir(projectPath);
            if (_templatesDir == null || !Directory.Exists(_templatesDir)) return;

            try
            {
                foreach (var file in Directory.GetFiles(_templatesDir, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                        var entry = JsonSerializer.Deserialize<TaskTemplate>(json);
                        if (entry != null)
                            TaskTemplates.Add(entry);
                    }
                    catch (Exception ex) { AppLogger.Warn("SettingsManager", $"Failed to load template {file}", ex); }
                }
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to load task templates", ex); }
        }

        public void SaveTemplate(TaskTemplate template)
        {
            if (_templatesDir == null) return;
            try
            {
                if (!Directory.Exists(_templatesDir))
                    Directory.CreateDirectory(_templatesDir);

                var filePath = Path.Combine(_templatesDir, $"{template.Id}.json");
                var json = JsonSerializer.Serialize(template,
                    new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(filePath, json, "SettingsManager");
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to save template", ex); }
        }

        public void DeleteTemplate(TaskTemplate template)
        {
            if (_templatesDir == null) return;
            try
            {
                var filePath = Path.Combine(_templatesDir, $"{template.Id}.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to delete template", ex); }
        }

        private string? ResolveTemplatesDir(string? projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            var dir = Path.Combine(projectPath, ".spritely", "templates");

            // One-time migration: copy global templates into project as individual files
            if (!Directory.Exists(dir) && File.Exists(_globalTemplatesFile))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var json = File.ReadAllText(_globalTemplatesFile);
                    var entries = JsonSerializer.Deserialize<List<TaskTemplate>>(json);
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                        {
                            var filePath = Path.Combine(dir, $"{entry.Id}.json");
                            var entryJson = JsonSerializer.Serialize(entry,
                                new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(filePath, entryJson);
                        }
                    }
                    AppLogger.Info("SettingsManager", $"Migrated {entries?.Count ?? 0} global templates to {dir}");
                }
                catch (Exception ex) { AppLogger.Warn("SettingsManager", "Failed to migrate templates", ex); }
            }

            return dir;
        }
    }
}
