using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HappyEngine.Helpers;
using HappyEngine.Models;

namespace HappyEngine.Managers
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

        private readonly DispatcherTimer _healthCheckTimer;
        private readonly string _projectPath;
        private readonly ProjectManager _projectManager;
        private McpHealthStatus _currentStatus = McpHealthStatus.Disconnected;
        private int _consecutiveFailures = 0;
        private bool _isDisposed = false;
        private CancellationTokenSource _cts = new();

        public McpHealthStatus McpStatus => _currentStatus;

        public McpHealthMonitor(string projectPath, ProjectManager projectManager)
        {
            _projectPath = projectPath;
            _projectManager = projectManager;

            _healthCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _healthCheckTimer.Tick += async (s, e) => await PerformHealthCheck();
        }

        public void Start()
        {
            AppLogger.Info("McpHealthMonitor", $"Starting health monitoring for project: {_projectPath}");
            _healthCheckTimer.Start();
            _ = PerformHealthCheck(); // Perform initial check immediately
        }

        public void Stop()
        {
            AppLogger.Info("McpHealthMonitor", $"Stopping health monitoring for project: {_projectPath}");
            _healthCheckTimer.Stop();
            UpdateStatus(McpHealthStatus.Disconnected);
        }

        private async Task PerformHealthCheck()
        {
            if (_isDisposed) return;

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null || entry.McpStatus != Models.McpStatus.Connected)
            {
                Stop(); // Stop monitoring if project is not connected
                return;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                // Create JSON-RPC ping request
                var jsonRequest = new
                {
                    jsonrpc = "2.0",
                    method = "ping",
                    @params = new { },
                    id = Guid.NewGuid().ToString()
                };

                var json = JsonSerializer.Serialize(jsonRequest);

                using var request = new HttpRequestMessage(HttpMethod.Post, entry.McpAddress);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Content = content;
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var httpClient = new HttpClient();
                using var response = await httpClient.SendAsync(request, timeoutCts.Token);

                // Check if we got a successful response
                if (response.IsSuccessStatusCode ||
                    response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    _consecutiveFailures = 0;
                    UpdateStatus(McpHealthStatus.Connected);
                }
                else
                {
                    HandleFailure($"HTTP {(int)response.StatusCode}");
                }
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
            AppLogger.Warn("McpHealthMonitor", $"MCP ping failed ({_consecutiveFailures}/3): {reason}");

            if (_consecutiveFailures >= 3)
            {
                UpdateStatus(McpHealthStatus.Disconnected);
                _ = AttemptReconnect();
            }
        }

        private async Task AttemptReconnect()
        {
            if (_isDisposed) return;

            UpdateStatus(McpHealthStatus.Reconnecting);
            AppLogger.Info("McpHealthMonitor", "Attempting to restart MCP server...");

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;

            try
            {
                // First, stop the existing MCP server
                _projectManager.StopMcpServer(entry);

                // Wait a bit for cleanup
                await Task.Delay(2000);

                // Check if Unity is running
                if (!IsUnityRunning())
                {
                    AppLogger.Warn("McpHealthMonitor", "Cannot restart MCP: Unity is not running");
                    entry.McpOutput?.AppendLine("⚠️ Cannot restart MCP: Unity Editor is not running");
                    _projectManager.NotifyMcpOutputChanged(_projectPath);
                    UpdateStatus(McpHealthStatus.Disconnected);
                    Stop(); // Stop monitoring since we can't reconnect
                    return;
                }

                // Restart the MCP server
                await _projectManager.ConnectMcpAsync(_projectPath);

                // Reset failure counter
                _consecutiveFailures = 0;

                // Status will be updated by the ConnectMcpAsync method
            }
            catch (Exception ex)
            {
                AppLogger.Error("McpHealthMonitor", "Failed to restart MCP server", ex);
                UpdateStatus(McpHealthStatus.Disconnected);
            }
        }

        private bool IsUnityRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("Unity");
                return processes.Length > 0;
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
            _healthCheckTimer.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}