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
    /// Manages MCP server lifecycle: connect, disconnect, health checks, and Unity detection.
    /// </summary>
    public class McpConfigManager
    {
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly IProjectDataProvider _data;
        private readonly ProjectColorManager _colorManager;

        public event Action<AgentTask>? McpInvestigationRequested;
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

                System.Windows.MessageBox.Show(
                    "Unity Editor must be running before connecting to MCP.\n\nPlease start Unity Editor and try again.",
                    "Unity Not Running",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);

                return;
            }

            var success = await StartMcpServerAsync(entry);

            if (!success)
            {
                var diagnosticSummary = entry.McpOutput.ToString();
                var investigateTask = new AgentTask
                {
                    Description = $"The MCP server failed to start after 3 retry attempts (30 seconds each). " +
                        $"Investigate and fix the root cause of the MCP connection failure. Verify:\n" +
                        $"1. Unity Editor is running with the MCP plugin installed\n" +
                        $"2. The MCP start command configured in McpStartCommand is correct\n" +
                        $"3. The MCP address (McpAddress) is accessible and not blocked by firewall/antivirus\n" +
                        $"4. The port is not already in use by another process\n" +
                        $"5. Check application logs for detailed error information\n\n" +
                        $"Configuration:\n" +
                        $"  Start command: {entry.McpStartCommand}\n" +
                        $"  Expanded: {Environment.ExpandEnvironmentVariables(entry.McpStartCommand)}\n" +
                        $"  Address: {entry.McpAddress}\n" +
                        $"  Log file: {AppLogger.GetLogFilePath()}\n\n" +
                        $"Diagnostic output from startup attempts:\n{diagnosticSummary}",
                    SkipPermissions = true,
                    ProjectPath = projectPath,
                    ProjectColor = _colorManager.GetProjectColor(projectPath),
                    ProjectDisplayName = _colorManager.GetProjectDisplayName(projectPath)
                };
                McpInvestigationRequested?.Invoke(investigateTask);
            }

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

        public async Task<bool> CheckMcpHealth(string url)
        {
            try
            {
                var jsonRequest = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                            experimental = new { }
                        },
                        clientInfo = new
                        {
                            name = "Spritely",
                            version = "1.0.0"
                        }
                    },
                    id = 1
                };

                var json = System.Text.Json.JsonSerializer.Serialize(jsonRequest);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = content;
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                    response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    AppLogger.Debug("McpConfigManager", $"Health check returned {response.StatusCode} for {url}");
                    return false;
                }

                // Parse response body to verify Unity is actually connected
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(responseBody))
                {
                    var lower = responseBody.ToLowerInvariant();
                    if (lower.Contains("no unity") || lower.Contains("not connected") || lower.Contains("no editor"))
                    {
                        AppLogger.Warn("McpConfigManager", $"MCP server running but Unity not connected: {responseBody}");
                        return false;
                    }

                    // Check for JSON-RPC error responses
                    if (lower.Contains("\"error\""))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                            {
                                var errorMsg = errorProp.ToString();
                                AppLogger.Warn("McpConfigManager", $"MCP health check got error response: {errorMsg}");
                                return false;
                            }
                        }
                        catch { /* Not valid JSON, continue */ }
                    }
                }

                return response.IsSuccessStatusCode;
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

        private void KillProcessOnPort(int port, ProjectEntry entry)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano | findstr \"LISTENING\" | findstr \":{port} \"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        try
                        {
                            var existing = System.Diagnostics.Process.GetProcessById(pid);
                            if (!existing.HasExited)
                            {
                                AppLogger.Info("McpConfigManager", $"Killing stale process {pid} ({existing.ProcessName}) on port {port}");
                                entry.McpOutput.AppendLine($"Killing stale process on port {port} (PID {pid})...");
                                existing.Kill();
                                existing.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("McpConfigManager", $"Error checking port {port}", ex);
            }
        }

        private string? GetPortListenerInfo(int port)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano | findstr \"LISTENING\" | findstr \":{port} \"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        try
                        {
                            var existing = System.Diagnostics.Process.GetProcessById(pid);
                            return $"PID {pid} ({existing.ProcessName})";
                        }
                        catch { return $"PID {pid} (unknown)"; }
                    }
                }
            }
            catch { }
            return null;
        }

        private bool IsCommandAccessible(string mcpStartCommand)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(mcpStartCommand);
                var executable = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(executable)) return false;

                if (System.IO.File.Exists(executable)) return true;

                // Check if it's on PATH
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c where \"{executable}\" 2>nul",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                return !string.IsNullOrEmpty(output);
            }
            catch { return false; }
        }

        private async Task<bool> IsAddressReachable(string mcpAddress)
        {
            try
            {
                if (!Uri.TryCreate(mcpAddress, UriKind.Absolute, out var uri)) return false;
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await client.GetAsync(mcpAddress);
                // Any response (even error) means the address is reachable
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private string BuildDiagnosticReport(ProjectEntry entry, string? lastHealthError)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== MCP Startup Diagnostic Report ===");
            sb.AppendLine();

            // 1. Unity Editor check
            var unityRunning = IsUnityRunning();
            sb.AppendLine($"[{(unityRunning ? "PASS" : "FAIL")}] Unity Editor running: {(unityRunning ? "Yes" : "No")}");
            if (!unityRunning)
                sb.AppendLine("  → Start Unity Editor with the MCP for Unity plugin installed before connecting.");

            // 2. Start command check
            var expanded = Environment.ExpandEnvironmentVariables(entry.McpStartCommand);
            var cmdAccessible = IsCommandAccessible(entry.McpStartCommand);
            sb.AppendLine($"[{(cmdAccessible ? "PASS" : "FAIL")}] Start command executable found: {(cmdAccessible ? "Yes" : "No")}");
            sb.AppendLine($"  Command: {entry.McpStartCommand}");
            sb.AppendLine($"  Expanded: {expanded}");
            var resolvedCommand = BuildStartCommand(entry);
            if (!string.IsNullOrWhiteSpace(resolvedCommand) &&
                !string.Equals(resolvedCommand, expanded, StringComparison.Ordinal))
            {
                sb.AppendLine($"  Resolved (with MCP address): {resolvedCommand}");
            }
            if (!cmdAccessible)
                sb.AppendLine("  → The executable in the start command was not found. Verify the path and that the tool is installed.");

            // 3. Port in use check
            int port = 8080;
            if (Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri))
                port = uri.Port;
            var listener = GetPortListenerInfo(port);
            var portInUse = listener != null;
            sb.AppendLine($"[{(portInUse ? "WARN" : "PASS")}] Port {port} in use: {(portInUse ? $"Yes — {listener}" : "No")}");
            if (portInUse)
                sb.AppendLine("  → Another process is using this port. Kill it or configure a different port.");

            // 4. Address/network info
            sb.AppendLine($"[INFO] MCP address: {entry.McpAddress}");
            if (!string.IsNullOrEmpty(entry.McpActiveInstance))
                sb.AppendLine($"[INFO] Active Unity instance: {entry.McpActiveInstance}");
            if (entry.McpUnityPid > 0)
                sb.AppendLine($"[INFO] Unity Editor PID: {entry.McpUnityPid}");

            // 5. Last health check error
            if (!string.IsNullOrEmpty(lastHealthError))
            {
                sb.AppendLine($"[INFO] Last health check error: {lastHealthError}");
            }

            // 6. Process output
            var output = entry.McpOutput.ToString();
            if (!string.IsNullOrEmpty(output))
            {
                sb.AppendLine();
                sb.AppendLine("=== Server Process Output ===");
                // Include last 30 lines
                var lines = output.Split('\n');
                var start = Math.Max(0, lines.Length - 30);
                for (int i = start; i < lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                        sb.AppendLine(lines[i].TrimEnd());
                }
            }

            sb.AppendLine();
            sb.AppendLine($"Log file: {AppLogger.GetLogFilePath()}");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the effective MCP start command for a project entry, aligning the
        /// server's <c>--http-url</c> with the configured <see cref="ProjectEntry.McpAddress"/>
        /// and ensuring <c>--project-scoped-tools</c> is present.
        /// This mirrors the behavior of MCP for Unity's <c>ServerCommandBuilder</c> so that
        /// Spritely launches the server with settings consistent with the HTTP endpoint.
        /// </summary>
        private string BuildStartCommand(ProjectEntry entry)
        {
            var raw = entry.McpStartCommand ?? string.Empty;
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return expanded;
            }

            if (!Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                // If the MCP address is not a valid HTTP/HTTPS URL, fall back to the raw command.
                return expanded;
            }

            // Derive the base HTTP URL (scheme + host + port) from the configured MCP address.
            var baseBuilder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
            var httpBaseUrl = baseBuilder.Uri.ToString().TrimEnd('/');

            var updated = expanded;
            const string httpUrlFlag = "--http-url";
            string pattern = @"--http-url\s+(""(?:[^""]*)""|\S+)";

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    updated,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                // Replace any existing --http-url value so it always matches McpAddress.
                updated = System.Text.RegularExpressions.Regex.Replace(
                    updated,
                    pattern,
                    $"{httpUrlFlag} {httpBaseUrl}",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            else
            {
                // No explicit --http-url flag present; append one derived from McpAddress.
                updated = $"{updated} {httpUrlFlag} {httpBaseUrl}";
            }

            // Ensure project-scoped tools are enabled to mirror the Unity editor default.
            if (!updated.Contains("--project-scoped-tools", StringComparison.OrdinalIgnoreCase))
            {
                updated = $"{updated} --project-scoped-tools";
            }

            return updated;
        }

        private async Task<bool> StartMcpServerAsync(ProjectEntry entry)
        {
            const int MAX_RETRIES = 3;
            const int SERVER_START_TIMEOUT_SECONDS = 30;
            string? lastHealthError = null;

            try
            {
                entry.McpStatus = McpStatus.Connecting;
                entry.McpOutput.Clear();
                entry.McpOutput.AppendLine($"Connecting to server...");
                _data.SaveProjects();
                _data.RefreshProjectList(null, null, null);

                // Pre-flight: validate start command
                var expanded = Environment.ExpandEnvironmentVariables(entry.McpStartCommand);
                AppLogger.Info("McpConfigManager", $"Start command (expanded): {expanded}");
                if (!IsCommandAccessible(entry.McpStartCommand))
                {
                    AppLogger.Warn("McpConfigManager", $"Start command executable not found: {expanded}");
                    entry.McpOutput.AppendLine($"⚠ Start command executable not found: {expanded}");
                    entry.McpOutput.AppendLine($"  Verify the tool is installed and the path is correct.");
                }

                AppLogger.Info("McpConfigManager", $"Checking if MCP server is already running at {entry.McpAddress}");
                entry.McpOutput.AppendLine($"Checking if MCP server is already running at {entry.McpAddress}");

                if (await CheckMcpHealth(entry.McpAddress))
                {
                    AppLogger.Info("McpConfigManager", "MCP server is already running, connecting...");
                    entry.McpOutput.AppendLine($"Server already running, verifying connection...");

                    await RegisterMcpWithClaudeAsync(entry.McpServerName, entry.McpAddress);
                    await BindUnityInstanceAsync(entry);

                    entry.McpStatus = McpStatus.Connected;
                    entry.McpOutput.AppendLine($"✓ Connection verified - MCP server is ready!");
                    entry.McpOutput.AppendLine($"Unity operations available: create scene items, make prefabs, take screenshots");
                    _data.SaveProjects();
                    _data.RefreshProjectList(null, null, null);
                    return true;
                }

                if (Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri))
                {
                    var existingListener = GetPortListenerInfo(uri.Port);
                    if (existingListener != null)
                    {
                        AppLogger.Info("McpConfigManager", $"Port {uri.Port} occupied by {existingListener}, killing...");
                        entry.McpOutput.AppendLine($"Port {uri.Port} occupied by {existingListener}, clearing...");
                    }
                    KillProcessOnPort(uri.Port, entry);
                    await Task.Delay(500);
                }

                // Find the Unity Editor PID for this project (used for diagnostics)
                var unityPid = FindUnityPidForProject(entry.Path);
                entry.McpUnityPid = unityPid;
                if (unityPid > 0)
                {
                    AppLogger.Info("McpConfigManager", $"Binding MCP server to Unity PID {unityPid}");
                    entry.McpOutput.AppendLine($"Found Unity Editor (PID {unityPid}) for this project");
                }
                else
                {
                    AppLogger.Warn("McpConfigManager", "Could not determine Unity PID — server will start without PID binding");
                    entry.McpOutput.AppendLine("⚠ Could not determine Unity PID — starting without PID binding");
                }

                for (int retry = 1; retry <= MAX_RETRIES; retry++)
                {
                    AppLogger.Info("McpConfigManager", $"Attempting to start MCP server (attempt {retry}/{MAX_RETRIES})");
                    entry.McpOutput.AppendLine($"Starting MCP server (attempt {retry}/{MAX_RETRIES})...");

                    var startCommand = BuildStartCommand(entry);
                    AppLogger.Info("McpConfigManager", $"Start command: {startCommand}");

                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {startCommand}",
                        WorkingDirectory = entry.Path,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    var startTask = Task.Run(() =>
                    {
                        try
                        {
                            var process = System.Diagnostics.Process.Start(processInfo);
                            if (process != null)
                            {
                                entry.McpProcessId = process.Id;
                                entry.McpProcess = process;

                                process.OutputDataReceived += (sender, args) =>
                                {
                                    if (!string.IsNullOrEmpty(args.Data))
                                    {
                                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        {
                                            entry.McpOutput.AppendLine(args.Data);
                                            McpOutputChanged?.Invoke(entry.Path);
                                        });
                                        AppLogger.Debug("McpConfigManager", $"MCP server output: {args.Data}");
                                    }
                                };

                                process.ErrorDataReceived += (sender, args) =>
                                {
                                    if (!string.IsNullOrEmpty(args.Data))
                                    {
                                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        {
                                            entry.McpOutput.AppendLine(args.Data);
                                            McpOutputChanged?.Invoke(entry.Path);
                                        });
                                        AppLogger.Warn("McpConfigManager", $"MCP server error: {args.Data}");
                                    }
                                };

                                process.BeginOutputReadLine();
                                process.BeginErrorReadLine();

                                return true;
                            }
                            return false;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("McpConfigManager", $"Failed to start process on attempt {retry}", ex);
                            entry.McpOutput.AppendLine($"Failed to start process: {ex.Message}");
                            return false;
                        }
                    });

                    var processStarted = await startTask;
                    if (!processStarted)
                    {
                        AppLogger.Warn("McpConfigManager", $"Failed to start MCP server process on attempt {retry}");
                        entry.McpOutput.AppendLine($"⚠ Process failed to start on attempt {retry}");
                        if (retry < MAX_RETRIES)
                        {
                            await Task.Delay(2000);
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }

                    AppLogger.Info("McpConfigManager", $"Waiting for MCP server to start (up to {SERVER_START_TIMEOUT_SECONDS} seconds)...");
                    entry.McpOutput.AppendLine($"Waiting for server to respond (up to {SERVER_START_TIMEOUT_SECONDS}s)...");

                    var startTime = DateTime.Now;
                    int healthCheckCount = 0;
                    while ((DateTime.Now - startTime).TotalSeconds < SERVER_START_TIMEOUT_SECONDS)
                    {
                        healthCheckCount++;
                        if (await CheckMcpHealth(entry.McpAddress))
                        {
                            AppLogger.Info("McpConfigManager", $"MCP server started successfully after {(DateTime.Now - startTime).TotalSeconds:F1} seconds");
                            entry.McpOutput.AppendLine($"Server started successfully!");
                            entry.McpOutput.AppendLine($"Verifying MCP connection...");

                            await RegisterMcpWithClaudeAsync(entry.McpServerName, entry.McpAddress);
                            await BindUnityInstanceAsync(entry);

                            entry.McpStatus = McpStatus.Connected;
                            entry.McpOutput.AppendLine($"✓ Connection verified - MCP server is ready!");
                            entry.McpOutput.AppendLine($"Unity operations available: create scene items, make prefabs, take screenshots");
                            _data.SaveProjects();
                            _data.RefreshProjectList(null, null, null);
                            return true;
                        }

                        // Check if the process exited prematurely
                        if (entry.McpProcess != null && entry.McpProcess.HasExited)
                        {
                            var exitCode = entry.McpProcess.ExitCode;
                            lastHealthError = $"Server process exited prematurely with code {exitCode}";
                            AppLogger.Warn("McpConfigManager", lastHealthError);
                            entry.McpOutput.AppendLine($"⚠ {lastHealthError}");
                            break;
                        }

                        var elapsed = (DateTime.Now - startTime).TotalSeconds;
                        var delay = elapsed < 5 ? 1000 : elapsed < 15 ? 500 : 250;
                        await Task.Delay(delay);
                    }

                    var timeoutElapsed = (DateTime.Now - startTime).TotalSeconds;
                    if (lastHealthError == null)
                        lastHealthError = $"No response after {timeoutElapsed:F0}s ({healthCheckCount} health checks)";

                    AppLogger.Warn("McpConfigManager", $"MCP server failed to respond within {SERVER_START_TIMEOUT_SECONDS} seconds on attempt {retry} — {lastHealthError}");
                    entry.McpOutput.AppendLine($"⚠ Attempt {retry} failed: {lastHealthError}");

                    if (retry < MAX_RETRIES && entry.McpProcessId > 0)
                    {
                        try
                        {
                            var process = System.Diagnostics.Process.GetProcessById(entry.McpProcessId);
                            if (!process.HasExited)
                            {
                                process.Kill();
                                await Task.Delay(1000);
                            }
                        }
                        catch { }
                        entry.McpProcessId = 0;
                        lastHealthError = null;
                    }
                }

                // Build and log diagnostic report
                var diagnosticReport = BuildDiagnosticReport(entry, lastHealthError);
                AppLogger.Error("McpConfigManager", $"Failed to start MCP server after {MAX_RETRIES} attempts.\n{diagnosticReport}");
                entry.McpOutput.AppendLine();
                entry.McpOutput.AppendLine(diagnosticReport);

                entry.McpStatus = McpStatus.Failed;
                _data.SaveProjects();
                _data.RefreshProjectList(null, null, null);
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("McpConfigManager", "Unexpected error starting MCP server", ex);
                entry.McpOutput.AppendLine($"❌ Unexpected error: {ex.Message}");
                entry.McpStatus = McpStatus.Failed;
                _data.SaveProjects();
                _data.RefreshProjectList(null, null, null);
                return false;
            }
        }

        public void StopMcpServer(ProjectEntry entry)
        {
            try
            {
                if (entry.McpProcessId > 0 || entry.McpProcess != null)
                {
                    try
                    {
                        var process = entry.McpProcess ?? (entry.McpProcessId > 0 ? System.Diagnostics.Process.GetProcessById(entry.McpProcessId) : null);
                        if (process != null && !process.HasExited)
                        {
                            try
                            {
                                process.CancelOutputRead();
                                process.CancelErrorRead();
                            }
                            catch { }

                            process.Kill();
                            process.WaitForExit(5000);
                            process.Dispose();
                        }
                    }
                    catch { }

                    entry.McpProcessId = 0;
                    entry.McpProcess = null;
                }

                if (Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri))
                {
                    KillProcessOnPort(uri.Port, entry);
                }

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

        private async Task RegisterMcpWithClaudeAsync(string serverName, string mcpAddress = "http://127.0.0.1:8080/mcp")
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"mcp add --scope local --transport http {serverName} {mcpAddress}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = await Task.Run(() => System.Diagnostics.Process.Start(processInfo));
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpConfigManager", "Failed to register MCP server with Claude", ex);
            }
        }
    }
}
