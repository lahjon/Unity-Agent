using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using Spritely.Dialogs;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages project swap logic, callback coordination, and UI binding during transitions.
    /// </summary>
    public class ProjectSwitchManager
    {
        private readonly IProjectDataProvider _data;
        private bool _isSwapping;

        private Action<string>? _storedUpdateTerminal;
        private Action? _storedSaveSettings;
        private Action? _storedSyncSettings;

        public event Action? ProjectSwapStarted;
        public event Action? ProjectSwapCompleted;

        public bool IsSwapping => _isSwapping;
        public Action<string>? StoredUpdateTerminal => _storedUpdateTerminal;
        public Action? StoredSaveSettings => _storedSaveSettings;
        public Action? StoredSyncSettings => _storedSyncSettings;

        public ProjectSwitchManager(IProjectDataProvider data)
        {
            _data = data;
        }

        public void StoreCallbacks(
            Action<string>? updateTerminal,
            Action? saveSettings,
            Action? syncSettings)
        {
            if (updateTerminal != null) _storedUpdateTerminal = updateTerminal;
            if (saveSettings != null) _storedSaveSettings = saveSettings;
            if (syncSettings != null) _storedSyncSettings = syncSettings;
        }

        public void OnPromptProjectComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_data.View.PromptProjectLabel.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string newPath) return;
            if (newPath == _data.ProjectPath) return;
            if (_isSwapping) return;

            ExecuteSwap(newPath);
        }

        public void HandleCardSwap(string projPath)
        {
            if (projPath == _data.ProjectPath) return;
            if (_isSwapping) return;
            if (!DarkDialog.ShowConfirm("Are you sure you want to change project?", "Change Project"))
                return;

            ExecuteSwap(projPath);
        }

        private void ExecuteSwap(string newPath)
        {
            _isSwapping = true;
            _data.SetProjectPath(newPath);
            ProjectSwapStarted?.Invoke();

            var termCb = _storedUpdateTerminal;
            var saveCb = _storedSaveSettings;
            var syncCb = _storedSyncSettings;

            _data.View.ViewDispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    try { termCb?.Invoke(newPath); }
                    catch (Exception ex) { AppLogger.Warn("ProjectManager", "Terminal update failed during project swap", ex); }

                    await Task.Yield();
                    saveCb?.Invoke();
                    syncCb?.Invoke();
                }
                catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed during project swap", ex); }
                finally
                {
                    _isSwapping = false;
                    ProjectSwapCompleted?.Invoke();
                }
            }));
        }
    }
}
