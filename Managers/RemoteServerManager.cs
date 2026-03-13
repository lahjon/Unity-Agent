using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
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

    // External dependencies injected from MainWindow
    private readonly Func<ObservableCollection<AgentTask>> _getActiveTasks;
    private readonly Func<ObservableCollection<AgentTask>> _getHistoryTasks;
    private readonly Func<System.Collections.Generic.List<ProjectEntry>> _getProjects;
    private readonly Func<int> _getMaxConcurrent;
    private readonly Func<string, AgentTask?> _findTask;
    private readonly Action<CreateTaskRequest> _createTask;
    private readonly Action<AgentTask> _cancelTask;
    private readonly Action<AgentTask> _pauseTask;
    private readonly Action<AgentTask> _resumeTask;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }
    public string? ListenUrl { get; private set; }

    public event Action<string>? Log;
    public event Action<bool>? StatusChanged;

    public RemoteServerManager(
        Func<ObservableCollection<AgentTask>> getActiveTasks,
        Func<ObservableCollection<AgentTask>> getHistoryTasks,
        Func<System.Collections.Generic.List<ProjectEntry>> getProjects,
        Func<int> getMaxConcurrent,
        Func<string, AgentTask?> findTask,
        Action<CreateTaskRequest> createTask,
        Action<AgentTask> cancelTask,
        Action<AgentTask> pauseTask,
        Action<AgentTask> resumeTask)
    {
        _getActiveTasks = getActiveTasks;
        _getHistoryTasks = getHistoryTasks;
        _getProjects = getProjects;
        _getMaxConcurrent = getMaxConcurrent;
        _findTask = findTask;
        _createTask = createTask;
        _cancelTask = cancelTask;
        _pauseTask = pauseTask;
        _resumeTask = resumeTask;
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
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        try
        {
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            var method = req.HttpMethod;

            object? result = (method, path) switch
            {
                ("GET", "/api/status") => HandleStatus(),
                ("GET", "/api/projects") => HandleGetProjects(),
                ("GET", "/api/tasks") => HandleGetTasks(req),
                ("GET", var p) when p.StartsWith("/api/tasks/") => HandleGetTask(p),
                ("POST", "/api/tasks") => await HandleCreateTaskAsync(req),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/cancel") => HandleTaskAction(p, "cancel"),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/pause") => HandleTaskAction(p, "pause"),
                ("POST", var p) when p.StartsWith("/api/tasks/") && p.EndsWith("/resume") => HandleTaskAction(p, "resume"),
                _ => null
            };

            if (result == null)
            {
                await WriteJsonAsync(resp, 404, new ApiResponse<object> { Success = false, Error = "Not found" });
                return;
            }

            await WriteJsonAsync(resp, 200, result);
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
        var active = _getActiveTasks();
        return new ApiResponse<ServerStatusDto>
        {
            Data = new ServerStatusDto
            {
                ActiveTasks = active.Count(t => !t.IsFinished),
                MaxConcurrentTasks = _getMaxConcurrent()
            }
        };
    }

    private ApiResponse<System.Collections.Generic.List<ProjectDto>> HandleGetProjects()
    {
        var projects = _getProjects()
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

    private ApiResponse<System.Collections.Generic.List<TaskDto>> HandleGetTasks(HttpListenerRequest req)
    {
        var filter = req.QueryString["filter"] ?? "active";
        var tasks = filter == "history" ? _getHistoryTasks() : _getActiveTasks();

        var dtos = tasks.Select(MapTaskDto).ToList();
        return new ApiResponse<System.Collections.Generic.List<TaskDto>> { Data = dtos };
    }

    private ApiResponse<TaskDto>? HandleGetTask(string path)
    {
        // /api/tasks/{id}
        var id = path.Replace("/api/tasks/", "").Trim('/');
        var task = _findTask(id);
        if (task == null) return null;

        var dto = MapTaskDto(task, includeOutput: true);
        return new ApiResponse<TaskDto> { Data = dto };
    }

    private async Task<ApiResponse<TaskDto>> HandleCreateTaskAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var request = JsonSerializer.Deserialize<CreateTaskRequest>(body, JsonOpts);

        if (request == null || string.IsNullOrWhiteSpace(request.Description))
            return new ApiResponse<TaskDto> { Success = false, Error = "Description is required" };

        _createTask(request);
        return new ApiResponse<TaskDto> { Data = new TaskDto { Description = request.Description, Status = "Queued" } };
    }

    private ApiResponse<object>? HandleTaskAction(string path, string action)
    {
        // /api/tasks/{id}/{action}
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return null;

        var id = segments[2];
        var task = _findTask(id);
        if (task == null) return null;

        switch (action)
        {
            case "cancel": _cancelTask(task); break;
            case "pause": _pauseTask(task); break;
            case "resume": _resumeTask(task); break;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
