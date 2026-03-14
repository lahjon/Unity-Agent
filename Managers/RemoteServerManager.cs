using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Models;

namespace Spritely.Managers;

/// <summary>
/// Lightweight HTTP REST server that lets SpritelyRemote (Android) trigger
/// and monitor tasks over the local network.
/// </summary>
public sealed class RemoteServerManager : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private bool _disposed;

    private readonly IRemoteServerCallbacks _callbacks;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly string ApiKeyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spritely", "remote_api_key.txt");

    private static readonly string AuditLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spritely", "logs", "remote_audit.log");

    private int _auditCount;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }
    public string? ListenUrl { get; private set; }
    public string ApiKey { get; private set; } = "";
    public int AuditCount => _auditCount;

    public event Action<string>? Log;
    public event Action<bool>? StatusChanged;
    public event Action<int>? AuditCountChanged;

    public RemoteServerManager(IRemoteServerCallbacks callbacks)
    {
        _callbacks = callbacks;
        ApiKey = LoadOrGenerateApiKey();
    }

    private static string LoadOrGenerateApiKey()
    {
        try
        {
            if (File.Exists(ApiKeyFilePath))
            {
                var existing = File.ReadAllText(ApiKeyFilePath).Trim();
                if (existing.Length > 0) return existing;
            }

            var dir = Path.GetDirectoryName(ApiKeyFilePath)!;
            Directory.CreateDirectory(dir);

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            File.WriteAllText(ApiKeyFilePath, key);
            return key;
        }
        catch (Exception ex)
        {
            AppLogger.Error("RemoteServer", $"Failed to load/generate API key: {ex.Message}");
            return "";
        }
    }

    public void Start(int port)
    {
        if (IsRunning) return;

        Port = port;
        ListenUrl = $"http://+:{port}/";

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(ListenUrl);

        try
        {
            _listener.Start();
            Log?.Invoke($"Remote server started on port {port}");
            StatusChanged?.Invoke(true);
            _listenLoop = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch (HttpListenerException ex)
        {
            Log?.Invoke($"Failed to start server: {ex.Message}");
            _listener = null;
            StatusChanged?.Invoke(false);
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        Log?.Invoke("Remote server stopped");
        StatusChanged?.Invoke(false);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log?.Invoke($"Listen error: {ex.Message}"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // CORS for local dev
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Api-Key");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        // Validate API key
        if (!string.IsNullOrEmpty(ApiKey))
        {
            var providedKey = req.Headers["X-Api-Key"];
            if (string.IsNullOrEmpty(providedKey) || !string.Equals(providedKey, ApiKey, StringComparison.Ordinal))
            {
                await WriteJsonAsync(resp, 401, new ApiResponse<object> { Success = false, Error = "Unauthorized: invalid or missing API key" });
                return;
            }
        }

        try
        {
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            var method = req.HttpMethod;

            object? result = (method, path) switch
            {
                ("GET", "/api/status") => HandleStatus(),
                ("GET", "/api/settings") => HandleGetSettings(),
                ("GET", "/api/projects") => HandleGetProjects(),
                ("GET", "/api/tasks") => HandleGetTasks(req),
                ("GET", var p) when p.StartsWith("/api/tasks/") => HandleGetTask(p),
                ("POST", "/api/tasks") => await HandleCreateTaskAsync(req),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/cancel") => HandleTaskAction(p, "cancel", req),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/pause") => HandleTaskAction(p, "pause", req),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/resume") => HandleTaskAction(p, "resume", req),
                _ => null
            };

            if (result == null)
            {
                await WriteJsonAsync(resp, 404, new ApiResponse<object> { Success = false, Error = "Not found" });
                return;
            }

            var statusCode = result switch
            {
                ApiResponse<TaskDto> { Success: false } => 400,
                ApiResponse<TaskDto> { Location: not null, Success: true } => 201,
                _ => 200
            };
            await WriteJsonAsync(resp, statusCode, result);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Request error: {ex.Message}");
            await WriteJsonAsync(resp, 500, new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────

    private ApiResponse<ServerStatusDto> HandleStatus()
    {
        var active = _callbacks.GetActiveTasks();
        return new ApiResponse<ServerStatusDto>
        {
            Data = new ServerStatusDto
            {
                ActiveTasks = active.Count(t => !t.IsFinished),
                MaxConcurrentTasks = _callbacks.GetMaxConcurrentTasks()
            }
        };
    }

    private ApiResponse<AppSettingsDto> HandleGetSettings()
    {
        return new ApiResponse<AppSettingsDto> { Data = _callbacks.GetSettings() };
    }

    private ApiResponse<System.Collections.Generic.List<ProjectDto>> HandleGetProjects()
    {
        var projects = _callbacks.GetProjects()
            .Select(p => new ProjectDto
            {
                Name = p.Name,
                Path = p.Path,
                ShortDescription = p.ShortDescription ?? "",
                Color = p.Color ?? "",
                IsGame = p.IsGame
            }).ToList();

        return new ApiResponse<System.Collections.Generic.List<ProjectDto>> { Data = projects };
    }

    private ApiResponse<PaginatedResponse<TaskDto>> HandleGetTasks(HttpListenerRequest req)
    {
        var filter = req.QueryString["filter"] ?? "active";
        var tasks = filter == "history" ? _callbacks.GetHistoryTasks() : _callbacks.GetActiveTasks();

        IEnumerable<AgentTask> filtered = tasks;

        var statusFilter = req.QueryString["status"];
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var statuses = statusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(t => statuses.Any(s => string.Equals(s, t.Status.ToString(), StringComparison.OrdinalIgnoreCase)));
        }

        var projectPath = req.QueryString["projectPath"];
        if (!string.IsNullOrWhiteSpace(projectPath))
            filtered = filtered.Where(t => string.Equals(t.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));

        var allDtos = filtered.Select(t => MapTaskDto(t)).ToList();
        var totalCount = allDtos.Count;

        var page = Math.Max(1, int.TryParse(req.QueryString["page"], out var p) ? p : 1);
        var limit = int.TryParse(req.QueryString["limit"], out var l) ? Math.Clamp(l, 1, 100) : 20;
        var paged = allDtos.Skip((page - 1) * limit).Take(limit).ToList();

        return new ApiResponse<PaginatedResponse<TaskDto>>
        {
            Data = new PaginatedResponse<TaskDto>
            {
                Items = paged,
                TotalCount = totalCount,
                Page = page,
                PageSize = limit
            }
        };
    }

    private ApiResponse<TaskDto>? HandleGetTask(string path)
    {
        // /api/tasks/{id}
        var id = path.Replace("/api/tasks/", "").Trim('/');
        var task = _callbacks.FindTask(id);
        if (task == null) return null;

        var dto = MapTaskDto(task, includeOutput: true);
        return new ApiResponse<TaskDto> { Data = dto };
    }

    private async Task<ApiResponse<TaskDto>> HandleCreateTaskAsync(HttpListenerRequest req)
    {
        var clientIp = req.RemoteEndPoint?.ToString() ?? "unknown";
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var request = JsonSerializer.Deserialize<CreateTaskRequest>(body, JsonOpts);

        if (request == null || string.IsNullOrWhiteSpace(request.Description))
        {
            WriteAuditLog("create", "n/a", clientIp, false, "Description is required");
            return new ApiResponse<TaskDto> { Success = false, Error = "Description is required" };
        }

        if (request.Description.Length > 2000)
        {
            WriteAuditLog("create", "n/a", clientIp, false, "Description too long");
            return new ApiResponse<TaskDto> { Success = false, Error = "Description must be 2000 characters or fewer" };
        }

        if (!string.IsNullOrWhiteSpace(request.Model) && !Enum.TryParse<ModelType>(request.Model, ignoreCase: true, out _))
        {
            WriteAuditLog("create", "n/a", clientIp, false, $"Invalid model '{request.Model}'");
            return new ApiResponse<TaskDto> { Success = false, Error = $"Invalid model '{request.Model}'. Allowed: {string.Join(", ", Enum.GetNames<ModelType>())}" };
        }

        if (!Enum.TryParse<TaskPriority>(request.Priority, ignoreCase: true, out _))
        {
            WriteAuditLog("create", "n/a", clientIp, false, $"Invalid priority '{request.Priority}'");
            return new ApiResponse<TaskDto> { Success = false, Error = $"Invalid priority '{request.Priority}'. Allowed: {string.Join(", ", Enum.GetNames<TaskPriority>())}" };
        }

        var knownPaths = _callbacks.GetProjects().Select(p => p.Path).ToList();
        if (string.IsNullOrWhiteSpace(request.ProjectPath) || !knownPaths.Contains(request.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            WriteAuditLog("create", "n/a", clientIp, false, "Invalid projectPath");
            return new ApiResponse<TaskDto> { Success = false, Error = $"Invalid projectPath. Must be one of the registered projects: {string.Join(", ", knownPaths)}" };
        }

        var created = _callbacks.CreateTask(request);
        if (created == null)
        {
            WriteAuditLog("create", "n/a", clientIp, false, "Failed to create task");
            return new ApiResponse<TaskDto> { Success = false, Error = "Failed to create task" };
        }

        var dto = MapTaskDto(created);
        WriteAuditLog("create", dto.Id, clientIp, true);
        return new ApiResponse<TaskDto>
        {
            Data = dto,
            Location = $"/api/tasks/{dto.Id}"
        };
    }

    private ApiResponse<object>? HandleTaskAction(string path, string action, HttpListenerRequest req)
    {
        var clientIp = req.RemoteEndPoint?.ToString() ?? "unknown";

        // /api/tasks/{id}/{action}
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return null;

        var id = segments[2];
        var task = _callbacks.FindTask(id);
        if (task == null)
        {
            WriteAuditLog(action, id, clientIp, false, "Task not found");
            return null;
        }

        try
        {
            switch (action)
            {
                case "cancel": _callbacks.CancelTask(task); break;
                case "pause": _callbacks.PauseTask(task); break;
                case "resume": _callbacks.ResumeTask(task); break;
            }
            WriteAuditLog(action, id, clientIp, true);
        }
        catch (Exception ex)
        {
            WriteAuditLog(action, id, clientIp, false, ex.Message);
            throw;
        }

        return new ApiResponse<object> { Data = new { action, taskId = id } };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static TaskDto MapTaskDto(AgentTask task, bool includeOutput = false)
    {
        var d = task.Data;
        return new TaskDto
        {
            Id = d.Id,
            TaskNumber = d.TaskNumber,
            Description = d.Description,
            Status = d.Status.ToString(),
            StatusText = task.StatusText,
            ProjectPath = d.ProjectPath,
            ProjectName = Path.GetFileName(d.ProjectPath),
            Model = d.Model.ToString(),
            Priority = d.PriorityLevel.ToString(),
            IsFeatureMode = d.IsFeatureMode,
            StartTime = d.StartTime,
            EndTime = d.EndTime,
            CurrentIteration = d.CurrentIteration,
            MaxIterations = d.MaxIterations,
            IsVerified = d.IsVerified,
            IsCommitted = d.IsCommitted,
            ChangedFiles = d.ChangedFiles.ToList(),
            Output = includeOutput ? task.Runtime?.OutputBuilder?.ToString() : null
        };
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int statusCode, object data)
    {
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private void WriteAuditLog(string action, string taskId, string clientIp, bool success, string? error = null)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var status = success ? "SUCCESS" : "FAILURE";
        var errorPart = error != null ? $" error=\"{error}\"" : "";
        var line = $"[{timestamp}] {status} action={action} taskId={taskId} client={clientIp}{errorPart}{Environment.NewLine}";

        SafeFileWriter.WriteInBackground(AuditLogPath, line, "RemoteAudit", appendLine: true);

        var count = Interlocked.Increment(ref _auditCount);
        AuditCountChanged?.Invoke(count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
