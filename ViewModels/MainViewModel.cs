using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely.ViewModels
{
    /// <summary>
    /// Central ViewModel for MainWindow. Exposes observable collections and properties
    /// that were previously scattered across MainWindow partials, enabling data-binding
    /// from XAML and making UI logic testable without spinning up WPF windows.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // ── Observable Collections (owned by MainWindow, exposed here for binding) ──

        public ObservableCollection<AgentTask> ActiveTasks { get; }
        public ObservableCollection<AgentTask> HistoryTasks { get; }
        public ObservableCollection<AgentTask> StoredTasks { get; }
        public ObservableCollection<FeatureEntry> FeatureEntries { get; }

        // ── Collection Views (for filtering/sorting in XAML) ──

        public ICollectionView? ActiveView { get; set; }
        public ICollectionView? HistoryView { get; set; }
        public ICollectionView? StoredView { get; set; }
        public ICollectionView? FeaturesView { get; set; }

        // ── Synchronization locks (shared with managers that need cross-thread access) ──

        public object ActiveTasksLock { get; }
        public object HistoryTasksLock { get; }
        public object StoredTasksLock { get; }

        // ── Observable Properties ──

        private string _selectedProjectPath = "";
        public string SelectedProjectPath
        {
            get => _selectedProjectPath;
            set => SetField(ref _selectedProjectPath, value);
        }

        private string _selectedProjectName = "";
        public string SelectedProjectName
        {
            get => _selectedProjectName;
            set => SetField(ref _selectedProjectName, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetField(ref _isBusy, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        private int _runningCount;
        public int RunningCount
        {
            get => _runningCount;
            set
            {
                if (SetField(ref _runningCount, value))
                    IsBusy = value > 0;
            }
        }

        private int _queuedCount;
        public int QueuedCount
        {
            get => _queuedCount;
            set => SetField(ref _queuedCount, value);
        }

        private int _completedCount;
        public int CompletedCount
        {
            get => _completedCount;
            set => SetField(ref _completedCount, value);
        }

        private int _failedCount;
        public int FailedCount
        {
            get => _failedCount;
            set => SetField(ref _failedCount, value);
        }

        private FeatureEntry? _selectedFeature;
        public FeatureEntry? SelectedFeature
        {
            get => _selectedFeature;
            set => SetField(ref _selectedFeature, value);
        }

        private bool _featureOperationRunning;
        public bool FeatureOperationRunning
        {
            get => _featureOperationRunning;
            set => SetField(ref _featureOperationRunning, value);
        }

        // ── Constructor ──

        public MainViewModel()
            : this(new(), new(), new(), new(), new(), new(), new())
        {
        }

        /// <summary>
        /// Wraps existing collections and locks from MainWindow so all partials
        /// can continue using their current fields while XAML binds to the ViewModel.
        /// </summary>
        public MainViewModel(
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks,
            ObservableCollection<AgentTask> storedTasks,
            object activeTasksLock,
            object historyTasksLock,
            object storedTasksLock,
            ObservableCollection<FeatureEntry> featureEntries)
        {
            ActiveTasks = activeTasks;
            HistoryTasks = historyTasks;
            StoredTasks = storedTasks;
            ActiveTasksLock = activeTasksLock;
            HistoryTasksLock = historyTasksLock;
            StoredTasksLock = storedTasksLock;
            FeatureEntries = featureEntries;
        }

        /// <summary>
        /// Initializes collection views and cross-thread synchronization.
        /// Must be called from the UI thread after construction.
        /// </summary>
        public void InitializeCollectionViews()
        {
            BindingOperations.EnableCollectionSynchronization(ActiveTasks, ActiveTasksLock);
            BindingOperations.EnableCollectionSynchronization(HistoryTasks, HistoryTasksLock);
            BindingOperations.EnableCollectionSynchronization(StoredTasks, StoredTasksLock);

            ActiveView = CollectionViewSource.GetDefaultView(ActiveTasks);
            HistoryView = CollectionViewSource.GetDefaultView(HistoryTasks);
            StoredView = CollectionViewSource.GetDefaultView(StoredTasks);
            FeaturesView = CollectionViewSource.GetDefaultView(FeatureEntries);
        }

        /// <summary>
        /// Refreshes task status counts from the current collection state.
        /// Call from the status timer or after task state changes.
        /// </summary>
        public void RefreshStatusCounts()
        {
            lock (ActiveTasksLock)
            {
                RunningCount = ActiveTasks.Count(t => t.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored);
                QueuedCount = ActiveTasks.Count(t => t.Status is AgentTaskStatus.Queued or AgentTaskStatus.InitQueued);
            }

            lock (HistoryTasksLock)
            {
                CompletedCount = HistoryTasks.Count(t => t.Status == AgentTaskStatus.Completed);
                FailedCount = HistoryTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            }
        }

        /// <summary>
        /// Updates the selected project state from the ProjectManager.
        /// </summary>
        public void UpdateSelectedProject(string projectPath, string projectName)
        {
            SelectedProjectPath = projectPath;
            SelectedProjectName = projectName;
        }

        // ── INotifyPropertyChanged ──

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
