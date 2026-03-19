using System.Collections.Generic;
using System.Windows.Threading;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Shared state contract for ProjectManager sub-managers.
    /// Provides access to the project list, current project, persistence, and view.
    /// </summary>
    public interface IProjectDataProvider
    {
        List<ProjectEntry> SavedProjects { get; }
        string ProjectPath { get; }
        IProjectPanelView View { get; }
        void SetProjectPath(string path);
        void SaveProjects();
        void RefreshProjectCombo();
        void RefreshProjectList(System.Action<string>? updateTerminalWorkingDirectory, System.Action? saveSettings, System.Action? syncSettings);
        ProjectEntry? GetCurrentProject();
        void RemoveProject(string projectPath, System.Action<string> updateTerminalWorkingDirectory, System.Action saveSettings, System.Action syncSettings);
    }
}
