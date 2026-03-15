using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spritely.Models;

// ── Response wrappers ──────────────────────────────────────────────

public sealed class ApiResponse<T>
{
    [JsonPropertyName("success")] public bool Success { get; init; } = true;
    [JsonPropertyName("data")] public T? Data { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }
}

public sealed class PaginatedResponse<T>
{
    [JsonPropertyName("items")] public List<T> Items { get; init; } = [];
    [JsonPropertyName("totalCount")] public int TotalCount { get; init; }
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("pageSize")] public int PageSize { get; init; }
}

// ── DTOs ───────────────────────────────────────────────────────────

public sealed class ServerStatusDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = "Spritely";
    [JsonPropertyName("version")] public string Version { get; init; } = "1.0";
    [JsonPropertyName("activeTasks")] public int ActiveTasks { get; init; }
    [JsonPropertyName("maxConcurrentTasks")] public int MaxConcurrentTasks { get; init; }
}

public sealed class ProjectDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("shortDescription")] public string ShortDescription { get; init; } = "";
    [JsonPropertyName("color")] public string Color { get; init; } = "";
    [JsonPropertyName("isGame")] public bool IsGame { get; init; }
}

public sealed class TaskDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("taskNumber")] public int TaskNumber { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("statusText")] public string StatusText { get; init; } = "";
    [JsonPropertyName("projectPath")] public string ProjectPath { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("priority")] public string Priority { get; init; } = "";
    [JsonPropertyName("isTeamsMode")] public bool IsTeamsMode { get; init; }
    [JsonPropertyName("startTime")] public DateTime StartTime { get; init; }
    [JsonPropertyName("endTime")] public DateTime? EndTime { get; init; }
    [JsonPropertyName("currentIteration")] public int CurrentIteration { get; init; }
    [JsonPropertyName("maxIterations")] public int MaxIterations { get; init; }
    [JsonPropertyName("isVerified")] public bool IsVerified { get; init; }
    [JsonPropertyName("isCommitted")] public bool IsCommitted { get; init; }
    [JsonPropertyName("changedFiles")] public List<string> ChangedFiles { get; init; } = [];
    [JsonPropertyName("output")] public string? Output { get; init; }
}

public sealed class AppSettingsDto
{
    [JsonPropertyName("autoCommit")] public bool AutoCommit { get; init; }
    [JsonPropertyName("autoQueue")] public bool AutoQueue { get; init; }
    [JsonPropertyName("autoVerify")] public bool AutoVerify { get; init; }
    [JsonPropertyName("maxConcurrentTasks")] public int MaxConcurrentTasks { get; init; }
    [JsonPropertyName("taskTimeoutMinutes")] public int TaskTimeoutMinutes { get; init; }
    [JsonPropertyName("opusEffortLevel")] public string OpusEffortLevel { get; init; } = "high";
}

public sealed class CreateTaskRequest
{
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("projectPath")] public string ProjectPath { get; init; } = "";
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("priority")] public string Priority { get; init; } = "Normal";
    [JsonPropertyName("isTeamsMode")] public bool IsTeamsMode { get; init; }
    [JsonPropertyName("useMcp")] public bool UseMcp { get; init; }
    [JsonPropertyName("autoDecompose")] public bool AutoDecompose { get; init; }
    [JsonPropertyName("extendedPlanning")] public bool ExtendedPlanning { get; init; }
    [JsonPropertyName("autoQueue")] public bool? AutoQueue { get; init; }
}
