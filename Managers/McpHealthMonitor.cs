using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    public enum McpHealthStatus
    {
        Connected,
        Disconnected,
        Reconnecting
    }

    public class McpHealthMonitor : IDisposable
    {
        public event Action<McpHealthStatus, string>? McpStatusChanged;

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly string _projectPath;
        private readonly ProjectManager _projectManager;
        private McpHealthStatus _currentStatus = McpHealthStatus.Disconnected;
        private int _consecutiveFailures = 0;
        private bool _isDisposed = false;
        private CancellationTokenSource _cts = new();
        private Task? _monitorTask;

        public McpHealthStatus McpStatus => _currentStatus;

        public McpHealthMonitor(string projectPath, ProjectManager projectManager)
        {
            _projectPath = projectPath;
            _projectManager = projectManager;
        }

        public void Start()
        {
            AppLogger.Info("McpHealthMonitor", $"Starting health monitoring for project: {_projectPath}");
            _cts = new CancellationTokenSource();
            _monitorTask = RunMonitorLoop(_cts.Token);
        }

        public void Stop()
        {
            AppLogger.Info("McpHealthMonitor", $"Stopping health monitoring for project: {_projectPath}");
            _cts.Cancel();
            UpdateStatus(McpHealthStatus.Disconnected);
        }

        private async Task RunMonitorLoop(CancellationToken ct)
        {
            // Perform initial check immediately
            await PerformHealthCheck(ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await PerformHealthCheck(ct);
            }
        }

        private async Task PerformHealthCheck(CancellationToken ct)
        {
            if (_isDisposed || ct.IsCancellationRequested) return;

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null || entry.McpStatus != Models.McpStatus.Connected)
            {
                Stop();
                return;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                var jsonRequest = new
                {
                    jsonrpc = "2.0",
                    method = "ping",
                    @params = new { },
                    id = Guid.NewGuid().ToString()
                };

                var json = JsonSerializer.Serialize(jsonRequest);

                using var request = new HttpRequestMessage(HttpMethod.Post, entry.McpAddress);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    // Check response body for Unity disconnection indicators
                    var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    var lower = responseBody?.ToLowerInvariant() ?? "";
                    if (lower.Contains("no unity") || lower.Contains("not connected") || lower.Contains("no editor"))
                    {
                        HandleFailure("Unity not connected to MCP server");
                        return;
                    }

                    _consecutiveFailures = 0;
                    UpdateStatus(McpHealthStatus.Connected);
                }
                else
                {
                    HandleFailure($"HTTP {(int)response.StatusCode}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Monitor stopping, don't treat as failure
            }
            catch (Exception ex)
            {
                AppLogger.Warn("McpHealthMonitor", $"Health check failed: {ex.Message}");
                HandleFailure(ex.Message);
            }
        }

        private void HandleFailure(string reason)
        {
            _consecutiveFailures++;
            AppLogger.Warn("McpHealthMonitor", $"MCP ping failed ({_consecutiveFailures}/3) for {_projectPath}: {reason}");

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.McpOutput?.AppendLine($"⚠ Health check failed ({_consecutiveFailures}/3): {reason}");
                _projectManager.NotifyMcpOutputChanged(_projectPath);
            }

            if (_consecutiveFailures >= 3)
            {
                AppLogger.Warn("McpHealthMonitor", $"3 consecutive health check failures for {_projectPath}, triggering reconnect");
                UpdateStatus(McpHealthStatus.Disconnected);
                _ = AttemptReconnect();
            }
        }

        private async Task AttemptReconnect()
        {
            if (_isDisposed) return;

            UpdateStatus(McpHealthStatus.Reconnecting);
            AppLogger.Info("McpHealthMonitor", $"Attempting to restart MCP server for {_projectPath}...");

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;

            try
            {
                entry.McpOutput?.AppendLine("Stopping existing MCP server for reconnect...");
                _projectManager.NotifyMcpOutputChanged(_projectPath);
                _projectManager.StopMcpServer(entry);
                await Task.Delay(2000);

                if (!IsUnityRunning())
                {
                    AppLogger.Warn("McpHealthMonitor", "Cannot restart MCP: Unity is not running");
                    entry.McpOutput?.AppendLine("⚠ Cannot restart MCP: Unity Editor is not running.");
                    entry.McpOutput?.AppendLine("  Start Unity Editor and reconnect manually.");
                    _projectManager.NotifyMcpOutputChanged(_projectPath);
                    UpdateStatus(McpHealthStatus.Disconnected);
                    Stop();
                    return;
                }

                entry.McpOutput?.AppendLine("Unity is running, attempting reconnect...");
                _projectManager.NotifyMcpOutputChanged(_projectPath);

                await _projectManager.ConnectMcpAsync(_projectPath);
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                AppLogger.Error("McpHealthMonitor", $"Failed to restart MCP server for {_projectPath}", ex);
                entry.McpOutput?.AppendLine($"❌ Reconnect failed: {ex.Message}");
                _projectManager.NotifyMcpOutputChanged(_projectPath);
                UpdateStatus(McpHealthStatus.Disconnected);
            }
        }

        private bool IsUnityRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("Unity");
                var running = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateStatus(McpHealthStatus newStatus)
        {
            if (_currentStatus != newStatus)
            {
                _currentStatus = newStatus;
                var message = newStatus switch
                {
                    McpHealthStatus.Connected => "MCP connection is healthy",
                    McpHealthStatus.Disconnected => "MCP connection lost",
                    McpHealthStatus.Reconnecting => "Attempting to reconnect MCP...",
                    _ => ""
                };
                McpStatusChanged?.Invoke(newStatus, message);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
