using System.Windows;
using System.Windows.Input;

namespace Spritely.Controls.Templates;

/// <summary>
/// Code-behind for TaskCardTemplates ResourceDictionary.
/// Forwards all event handlers to MainWindow where the actual logic lives.
/// </summary>
public partial class TaskCardTemplates : ResourceDictionary
{
    public TaskCardTemplates()
    {
        InitializeComponent();
    }

    private static MainWindow? Main => Application.Current?.MainWindow as MainWindow;

    // ── ActiveTaskTemplate: Border events ──
    private void TaskCard_PreviewMiddleDown(object sender, MouseButtonEventArgs e) =>
        Main?.TaskCard_PreviewMiddleDown(sender, e);
    private void TaskCard_MouseUp(object sender, MouseButtonEventArgs e) =>
        Main?.TaskCard_MouseUp(sender, e);
    private void TaskCard_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        Main?.TaskCard_PreviewMouseDown(sender, e);
    private void TaskCard_MouseMove(object sender, MouseEventArgs e) =>
        Main?.TaskCard_MouseMove(sender, e);
    private void TaskCard_DragOver(object sender, DragEventArgs e) =>
        Main?.TaskCard_DragOver(sender, e);
    private void TaskCard_Drop(object sender, DragEventArgs e) =>
        Main?.TaskCard_Drop(sender, e);

    // ── ActiveTaskTemplate: Button clicks ──
    private void ForceStartQueued_Click(object sender, RoutedEventArgs e) =>
        Main?.ForceStartQueued_Click(sender, e);
    private void Pause_Click(object sender, RoutedEventArgs e) =>
        Main?.Pause_Click(sender, e);
    private void SoftStop_Click(object sender, RoutedEventArgs e) =>
        Main?.SoftStop_Click(sender, e);
    private void VerifyTask_Click(object sender, RoutedEventArgs e) =>
        Main?.VerifyTask_Click(sender, e);
    private void CommitTask_Click(object sender, RoutedEventArgs e) =>
        Main?.CommitTask_Click(sender, e);
    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        Main?.Cancel_Click(sender, e);
    private void ToggleFileLock_Click(object sender, RoutedEventArgs e) =>
        Main?.ToggleFileLock_Click(sender, e);

    // ── Shared context menu clicks (Active + History + Stored) ──
    private void ContinueTask_Click(object sender, RoutedEventArgs e) =>
        Main?.ContinueTask_Click(sender, e);
    private void CopyPrompt_Click(object sender, RoutedEventArgs e) =>
        Main?.CopyPrompt_Click(sender, e);
    private void ExportTask_Click(object sender, RoutedEventArgs e) =>
        Main?.ExportTask_Click(sender, e);
    private void ViewOutput_Click(object sender, RoutedEventArgs e) =>
        Main?.ViewOutput_Click(sender, e);
    private void RevertTask_Click(object sender, RoutedEventArgs e) =>
        Main?.RevertTask_Click(sender, e);
    private void CloneTask_Click(object sender, RoutedEventArgs e) =>
        Main?.CloneTask_Click(sender, e);
    private void RetryTask_Click(object sender, RoutedEventArgs e) =>
        Main?.RetryTask_Click(sender, e);
    private void StoreHistoryTask_Click(object sender, RoutedEventArgs e) =>
        Main?.StoreHistoryTask_Click(sender, e);

    // ── HistoryTaskTemplate ──
    private void Resume_Click(object sender, RoutedEventArgs e) =>
        Main?.Resume_Click(sender, e);
    private void RerunTask_Click(object sender, RoutedEventArgs e) =>
        Main?.RerunTask_Click(sender, e);
    private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e) =>
        Main?.RemoveHistoryTask_Click(sender, e);

    // ── StoredTaskTemplate ──
    private void CopyStoredPrompt_Click(object sender, RoutedEventArgs e) =>
        Main?.CopyStoredPrompt_Click(sender, e);
    private void RemoveStoredTask_Click(object sender, RoutedEventArgs e) =>
        Main?.RemoveStoredTask_Click(sender, e);
    private void ViewStoredTask_Click(object sender, RoutedEventArgs e) =>
        Main?.ViewStoredTask_Click(sender, e);
    private void ExecuteStoredTask_Click(object sender, RoutedEventArgs e) =>
        Main?.ExecuteStoredTask_Click(sender, e);

    // ── SavedPromptTemplate ──
    private void SavedPromptCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        Main?.SavedPromptCard_MouseLeftButtonDown(sender, e);
    private void SavedPromptCard_MouseDown(object sender, MouseButtonEventArgs e) =>
        Main?.SavedPromptCard_MouseDown(sender, e);
    private void SavedPromptRun_Click(object sender, RoutedEventArgs e) =>
        Main?.SavedPromptRun_Click(sender, e);
    private void SavedPromptCopy_Click(object sender, RoutedEventArgs e) =>
        Main?.SavedPromptCopy_Click(sender, e);
    private void DeleteSavedPrompt_Click(object sender, RoutedEventArgs e) =>
        Main?.DeleteSavedPrompt_Click(sender, e);
}
