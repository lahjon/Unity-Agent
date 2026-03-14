using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Spritely.Models;

/// <summary>
/// Callbacks from RemoteServerManager into the UI/orchestration layer.
/// Implemented by MainWindow to decouple the server from concrete UI dependencies.
/// </summary>
public interface IRemoteServerCallbacks
{
    AppSettingsDto GetSettings();
    ObservableCollection<AgentTask> GetActiveTasks();
    ObservableCollection<AgentTask> GetHistoryTasks();
    List<ProjectEntry> GetProjects();
    int GetMaxConcurrentTasks();
    AgentTask? FindTask(string id);
    AgentTask? CreateTask(CreateTaskRequest request);
    void CancelTask(AgentTask task);
    void PauseTask(AgentTask task);
    void ResumeTask(AgentTask task);
}
