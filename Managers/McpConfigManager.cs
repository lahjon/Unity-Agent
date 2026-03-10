using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages MCP connection lifecycle: checks if server is running, health checks, and Unity detection.
    /// Does NOT start the MCP server — expects it to already be running (e.g. started by Unity Editor plugin).
    /// </summary>
    public class McpConfigManager
    {
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly IProjectDataProvider _data;
        private readonly ProjectColorManager _colorManager;

        public event Action<string>? McpOutputChanged;

        public McpConfigManager(IProjectDataProvider data, ProjectColorManager colorManager)
        {
            _data = data;
            _colorManager = colorManager;
        }

        public void UpdateMcpToggleForProject()
        {
            var proj = _data.SavedProjects.Find(p => p.Path == _data.ProjectPath);
            if (proj != null)
            {
                _data.View.UseMcpToggle.IsEnabled = true;
                _data.View.UseMcpToggle.IsChecked = proj.IsGame && proj.McpStatus == McpStatus.Connected;
                _data.View.UseMcpToggle.Opacity = 1.0;
                _data.View.UseMcpToggle.ToolTip = proj.IsGame && proj.McpStatus == McpStatus.Connected
                    ? "MCP is connected and will be used for Unity-specific commands"
                    : "Enable to use MCP for this project";
            }
            else
            {
                _data.View.UseMcpToggle.IsChecked = false;
                _data.View.UseMcpToggle.IsEnabled = false;
                _data.View.UseMcpToggle.Opacity = 0.4;
                _data.View.UseMcpToggle.ToolTip = null;
            }
        }

        public async Task ConnectMcpAsync(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            if (!IsUnityRunning())
            {
                entry.McpOutput.Clear();
                entry.McpOutput.AppendLine("❌ Unity Editor is not running!");
                entry.McpOutput.AppendLine("");
                entry.McpOutput.AppendLine("MCP connection requires Unity Editor to be running.");
                entry.McpOutput.AppendLine("Please start Unity Editor and try again.");

                _data.RefreshProjectList(null, null, null);

                MessageBox.Show(
                    "Unity Editor must be running before connecting to MCP.\n\nPlease start Unity Editor and try again.",
                    "Unity Not Running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            entry.McpStatus = McpStatus.Connecting;
            entry.McpOutput.Clear();
            entry.McpOutput.AppendLine($"Checking MCP server at {entry.McpAddress}...");
            _data.SaveProjects();
            _data.RefreshProjectList(null, null, null);

            if (!await CheckMcpHealth(entry.McpAddress))
            {
                entry.McpOutput.AppendLine($"❌ MCP server is not running at {entry.McpAddress}");
                entry.McpOutput.AppendLine("");
                entry.McpOutput.AppendLine("The MCP server must be started from within Unity Editor.");
                entry.McpOutput.AppendLine("Ensure the MCP for Unity package is installed and the server is enabled.");
                entry.McpStatus = McpStatus.Failed;
                _data.SaveProjects();
                _data.RefreshProjectList(null, null, null);

                MessageBox.Show(
                    $"MCP server is not running at {entry.McpAddress}.\n\n" +
                    "The MCP server must be started from within Unity Editor.\n" +
                    "Ensure the MCP for Unity package is installed and the server is enabled.",
                    "MCP Server Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                UpdateMcpToggleForProject();
                return;
            }

            entry.McpOutput.AppendLine("Server found, verifying connection...");

            await RegisterMcpWithClaudeAsync(entry.McpServerName, entry.Path, entry.McpAddress);
            await BindUnityInstanceAsync(entry);

            var unityPid = FindUnityPidForProject(entry.Path);
            entry.McpUnityPid = unityPid;
            if (unityPid > 0)
                entry.McpOutput.AppendLine($"Found Unity Editor (PID {unityPid}) for this project");

            entry.McpStatus = McpStatus.Connected;
            entry.McpOutput.AppendLine($"✓ Connection verified - MCP server is ready!");
            entry.McpOutput.AppendLine($"Unity operations available: create scene items, make prefabs, take screenshots");
            _data.SaveProjects();
            _data.RefreshProjectList(null, null, null);
            UpdateMcpToggleForProject();
        }

        public void DisconnectMcp(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            StopMcpServer(entry);
            UpdateMcpToggleForProject();
        }

        public bool IsUnityRunning()
        {
            try
            {
                var unityProcesses = System.Diagnostics.Process.GetProcessesByName("Unity");

                if (unityProcesses.Length > 0)
                {
                    AppLogger.Info("McpConfigManager", $"Found {unityProcesses.Length} Unity process(es) running");
                    foreach (var process in unityProcesses)
                        process.Dispose();
                    return true;
                }

                var unityHubProcesses = System.Diagnostics.Process.GetProcessesByName("Unity Hub");
                if (unityHubProcesses.Length > 0)
                {
                    AppLogger.Debug("McpConfigManager", "Unity Hub is running, but Unity Editor itself is not");
                    foreach (var process in unityHubProcesses)
                        process.Dispose();
                }

                AppLogger.Info("McpConfigManager", "No Unity processes found");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("McpConfigManager", "Error checking if Unity is running", ex);
                return true;
            }
        }

        /// <summary>
        /// Finds the Unity Editor PID that has the given project path open.
        /// Uses WMI to match the -projectPath argument in the Unity process command line.
        /// Returns 0 if no matching Unity process is found.
        /// </summary>
        public int FindUnityPidForProject(string projectPath)
        {
            try
            {
                var normalizedProject = System.IO.Path.GetFullPath(projectPath).TrimEnd('\\', '/');
                var unityProcesses = System.Diagnostics.Process.GetProcessesByName("Unity");

                if (unityProcesses.Length == 0)
                    return 0;

                // If only one Unity instance, it's the one we want
                if (unityProcesses.Length == 1)
                {
                    var pid = unityProcesses[0].Id;
                    AppLogger.Info("McpConfigManager", $"Single Unity instance found (PID {pid}), using it for project {projectPath}");
                    foreach (var p in unityProcesses) p.Dispose();
                    return pid;
                }

                // Multiple Unity instances — use WMI to match command line -projectPath
                foreach (var p in unityProcesses) p.Dispose();

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c wmic process where \"name='Unity.exe'\" get ProcessId,CommandLine /format:csv 2>nul",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return 0;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("Unity", StringComparison.OrdinalIgnoreCase)) continue;

                    // CSV format: Node,CommandLine,ProcessId
                    var cmdLine = line;
                    var projectPathIdx = cmdLine.IndexOf("-projectPath", StringComparison.OrdinalIgnoreCase);
                    if (projectPathIdx < 0) continue;

                    // Extract the path after -projectPath
                    var afterFlag = cmdLine.Substring(projectPathIdx + "-projectPath".Length).Trim();
                    // Handle quoted paths
                    string extractedPath;
                    if (afterFlag.StartsWith("\""))
                    {
                        var endQuote = afterFlag.IndexOf('"', 1);
                        extractedPath = endQuote > 0 ? afterFlag.Substring(1, endQuote - 1) : afterFlag.Trim('"');
                    }
                    else
                    {
                        var spaceIdx = afterFlag.IndexOf(' ');
                        extractedPath = spaceIdx > 0 ? afterFlag.Substring(0, spaceIdx) : afterFlag;
                    }

                    var normalizedExtracted = System.IO.Path.GetFullPath(extractedPath.Trim()).TrimEnd('\\', '/');
                    if (string.Equals(normalizedProject, normalizedExtracted, StringComparison.OrdinalIgnoreCase))
                    {
                        // Find PID — last CSV column
                        var lastComma = line.LastIndexOf(',');
                        if (lastComma >= 0)
                        {
                            var pidStr = line.Substring(lastComma + 1).Trim();
                            if (int.TryParse(pidStr, out var matchedPid))
                            {
                                AppLogger.Info("McpConfigManager", $"Found Unity PID {matchedPid} for project {projectPath}");
                                return matchedPid;
                            }
                        }
                    }
                }

                AppLogger.Warn("McpConfigManager", $"Could not match Unity PID to project path: {projectPath}");
                return 0;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpConfigManager", $"Error finding Unity PID for project: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Simple connectivity check: sends a JSON-RPC ping and treats any HTTP response as "server is up".
        /// Only connection failures or timeouts mean "server is down".
        /// </summary>
        public async Task<bool> CheckMcpHealth(string url)
        {
            try
            {
                var ping = System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    method = "ping",
                    @params = new { },
                    id = 1
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(ping, System.Text.Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);
                // Any HTTP response means the server is listening — even 400/405
                AppLogger.Debug("McpConfigManager", $"Health ping to {url} returned {(int)response.StatusCode}");
                return true;
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Debug("McpConfigManager", $"Health check connection failed for {url}: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException)
            {
                AppLogger.Debug("McpConfigManager", $"Health check timed out for {url}");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("McpConfigManager", $"MCP health check failed for {url}", ex);
                return false;
            }
        }

        public void StopMcpServer(ProjectEntry entry)
        {
            try
            {
                entry.McpStatus = McpStatus.NotConnected;
                entry.McpOutput.AppendLine($"Server disconnected.");
                _data.SaveProjects();
                _data.RefreshProjectList(null, null, null);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpConfigManager", "Error stopping MCP server", ex);
            }
        }

        public void StopAllMcpServers()
        {
            foreach (var entry in _data.SavedProjects.Where(p => p.McpStatus is McpStatus.Connected or McpStatus.Connecting))
            {
                StopMcpServer(entry);
            }
        }

        public void NotifyMcpOutputChanged(string projectPath)
        {
            McpOutputChanged?.Invoke(projectPath);
        }

        /// <summary>
        /// After the MCP server is healthy, queries for connected Unity instances and
        /// binds the correct one (matching the project's Unity PID) as the active instance.
        /// </summary>
        private async Task BindUnityInstanceAsync(ProjectEntry entry)
        {
            const int MAX_INSTANCE_RETRIES = 5;
            const int INSTANCE_RETRY_DELAY_MS = 2000;

            try
            {
                var instances = new System.Collections.Generic.List<string>();

                // Retry loop: Unity may take a few seconds to connect to the server after it starts
                for (int attempt = 1; attempt <= MAX_INSTANCE_RETRIES; attempt++)
                {
                    instances.Clear();

                    var listRequest = new
                    {
                        jsonrpc = "2.0",
                        method = "resources/read",
                        @params = new { uri = "mcpforunity://instances" },
                        id = 2
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(listRequest);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    using var request = new HttpRequestMessage(HttpMethod.Post, entry.McpAddress);
                    request.Content = content;
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    var response = await _httpClient.SendAsync(request);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        AppLogger.Info("McpConfigManager", $"Unity instances response (attempt {attempt}): {responseBody}");

                        // Extract Name@hash patterns from the response
                        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(responseBody, @"[\w\-\. ]+@[a-f0-9]+"))
                        {
                            instances.Add(match.Value);
                        }
                    }

                    if (instances.Count > 0)
                        break;

                    if (attempt < MAX_INSTANCE_RETRIES)
                    {
                        AppLogger.Info("McpConfigManager", $"No Unity instances yet (attempt {attempt}/{MAX_INSTANCE_RETRIES}), waiting {INSTANCE_RETRY_DELAY_MS}ms...");
                        entry.McpOutput.AppendLine($"Waiting for Unity to connect ({attempt}/{MAX_INSTANCE_RETRIES})...");
                        await Task.Delay(INSTANCE_RETRY_DELAY_MS);
                    }
                }

                if (instances.Count == 0)
                {
                    AppLogger.Warn("McpConfigManager", "No Unity instances found after retries");
                    entry.McpOutput.AppendLine("⚠ No Unity instances detected — Unity may need the COPLAY package installed");
                    return;
                }

                string? instanceName;

                // If only one instance, use it; otherwise try to match by project name
                if (instances.Count == 1)
                {
                    instanceName = instances[0];
                }
                else
                {
                    // Try to match by project folder name
                    var projectFolder = System.IO.Path.GetFileName(entry.Path);
                    instanceName = instances.FirstOrDefault(i =>
                        i.StartsWith(projectFolder, StringComparison.OrdinalIgnoreCase)) ?? instances[0];
                }

                AppLogger.Info("McpConfigManager", $"Setting active Unity instance: {instanceName}");
                entry.McpOutput.AppendLine($"Binding to Unity instance: {instanceName}");
                entry.McpActiveInstance = instanceName;

                // Call set_active_instance on the MCP server
                var setRequest = new
                {
                    jsonrpc = "2.0",
                    method = "tools/call",
                    @params = new
                    {
                        name = "set_active_instance",
                        arguments = new { instance_name = instanceName }
                    },
                    id = 3
                };

                var setJson = System.Text.Json.JsonSerializer.Serialize(setRequest);
                var setContent = new StringContent(setJson, System.Text.Encoding.UTF8, "application/json");

                using var setReq = new HttpRequestMessage(HttpMethod.Post, entry.McpAddress);
                setReq.Content = setContent;
                setReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                setReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                var setResponse = await _httpClient.SendAsync(setReq);
                var setResponseBody = await setResponse.Content.ReadAsStringAsync();
                AppLogger.Info("McpConfigManager", $"set_active_instance response: {setResponseBody}");

                if (setResponse.IsSuccessStatusCode)
                {
                    entry.McpOutput.AppendLine($"✓ Bound to Unity instance: {instanceName}");
                }
                else
                {
                    AppLogger.Warn("McpConfigManager", $"Failed to set active instance: {setResponseBody}");
                    entry.McpOutput.AppendLine($"⚠ Could not bind instance (server may handle routing automatically)");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpConfigManager", $"Failed to bind Unity instance: {ex.Message}");
                entry.McpOutput.AppendLine($"⚠ Instance binding skipped: {ex.Message}");
            }
        }

        private async Task RegisterMcpWithClaudeAsync(string serverName, string projectPath, string mcpAddress = "http://127.0.0.1:8080/mcp")
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"mcp add --scope local --transport http {serverName} {mcpAddress}",
                    WorkingDirectory = projectPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                AppLogger.Info("McpConfigManager", $"Registering MCP server '{serverName}' at {mcpAddress} (scope=local, cwd={projectPath})");

                var process = await Task.Run(() => System.Diagnostics.Process.Start(processInfo));
                if (process != null)
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        AppLogger.Warn("McpConfigManager",
                            $"claude mcp add exited with code {process.ExitCode}. stdout: {stdout?.Trim()} stderr: {stderr?.Trim()}");
                    }
                    else
                    {
                        AppLogger.Info("McpConfigManager", $"MCP server registered with Claude CLI for project: {projectPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpConfigManager", "Failed to register MCP server with Claude", ex);
            }
        }
    }
}
