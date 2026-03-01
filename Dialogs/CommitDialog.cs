using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HappyEngine.Managers;
using HappyEngine.Models;

namespace HappyEngine.Dialogs
{
    public class CommitResult
    {
        public string Message { get; set; } = "";
        public bool Cancelled { get; set; } = true;
    }

    public static class CommitDialog
    {
        public static CommitResult Show(List<GitFileChange> selectedFiles, string suggestedMessage)
        {
            var dlg = DarkDialogWindow.Create("Create Commit", 600, 500);

            var result = new CommitResult();
            var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Create Commit",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Subtitle showing selected files
            stack.Children.Add(new TextBlock
            {
                Text = $"Creating commit with {selectedFiles.Count} selected file{(selectedFiles.Count != 1 ? "s" : "")}",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // Commit message label
            stack.Children.Add(new TextBlock
            {
                Text = "Commit Message",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Commit message textbox
            var messageBox = new TextBox
            {
                Text = suggestedMessage,
                Background = (Brush)Application.Current.FindResource("BgSection"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Height = 100,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(messageBox);

            // Focus and select all text
            messageBox.Focus();
            messageBox.SelectAll();

            // Files list label
            stack.Children.Add(new TextBlock
            {
                Text = "Files to be committed:",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Scrollable files list
            var scrollViewer = new ScrollViewer
            {
                Height = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = (Brush)Application.Current.FindResource("BgSection"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var filesList = new StackPanel { Margin = new Thickness(8) };
            foreach (var file in selectedFiles.OrderBy(f => f.FilePath))
            {
                var fileRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

                // Status badge
                var statusBadge = new Border
                {
                    Background = GetStatusColor(file.Status),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = file.DisplayStatus,
                        Foreground = Brushes.White,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI")
                    }
                };
                DockPanel.SetDock(statusBadge, Dock.Left);
                fileRow.Children.Add(statusBadge);

                // File path
                fileRow.Children.Add(new TextBlock
                {
                    Text = file.FilePath,
                    Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                filesList.Children.Add(fileRow);
            }
            scrollViewer.Content = filesList;
            stack.Children.Add(scrollViewer);

            // Buttons
            var buttonPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            cancelButton.Click += (s, e) =>
            {
                result.Cancelled = true;
                dlg.DialogResult = false;
            };
            buttonPanel.Children.Add(cancelButton);

            var commitButton = new Button
            {
                Content = "Commit",
                Width = 80,
                Height = 28,
                IsDefault = true
            };
            commitButton.Click += (s, e) =>
            {
                result.Message = messageBox.Text.Trim();
                if (string.IsNullOrEmpty(result.Message))
                {
                    MessageBox.Show("Please enter a commit message.", "Commit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                result.Cancelled = false;
                dlg.DialogResult = true;
            };
            buttonPanel.Children.Add(commitButton);

            stack.Children.Add(buttonPanel);

            dlg.Content = stack;
            dlg.ShowDialog();

            return result;
        }

        private static Brush GetStatusColor(string status)
        {
            return status switch
            {
                "A" or "AM" or "??" => (Brush)Application.Current.FindResource("SuccessGreen"),
                "M" or "MM" => (Brush)Application.Current.FindResource("WarningAmber"),
                "D" => (Brush)Application.Current.FindResource("ErrorRed"),
                "R" => (Brush)Application.Current.FindResource("InfoBlue"),
                _ => (Brush)Application.Current.FindResource("TextSecondary")
            };
        }
    }
}