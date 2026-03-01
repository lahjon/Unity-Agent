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
            var stack = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };

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
                Margin = new Thickness(0, 0, 0, 6)
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
                Padding = new Thickness(10),
                Height = 100,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(messageBox);

            // Files list label
            stack.Children.Add(new TextBlock
            {
                Text = "Files to be committed:",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Scrollable files list with border for consistency
            var filesBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgSection"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var scrollViewer = new ScrollViewer
            {
                Height = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden
            };

            var filesList = new StackPanel { Margin = new Thickness(10) };
            foreach (var file in selectedFiles.OrderBy(f => f.FilePath))
            {
                var fileRow = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

                // Status badge
                var statusBadge = new Border
                {
                    Background = GetStatusColor(file.Status),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 2, 5, 2),
                    Margin = new Thickness(0, 0, 8, 0),
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
            filesBorder.Child = scrollViewer;
            stack.Children.Add(filesBorder);

            // Buttons
            var buttonPanel = new DockPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true,
                Background = (Brush)Application.Current.FindResource("BgSection"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                Style = Application.Current.TryFindResource("ModernButtonStyle") as Style
            };
            cancelButton.Click += (s, e) =>
            {
                result.Cancelled = true;
                dlg.DialogResult = false;
            };

            var commitButton = new Button
            {
                Content = "Commit",
                Width = 90,
                Height = 32,
                IsDefault = true,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Foreground = Brushes.White,
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                Style = Application.Current.TryFindResource("ModernButtonStyle") as Style
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

            DockPanel.SetDock(commitButton, Dock.Right);
            DockPanel.SetDock(cancelButton, Dock.Right);
            buttonPanel.Children.Add(commitButton);
            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(new TextBlock()); // Spacer

            stack.Children.Add(buttonPanel);

            // Set the body content instead of setting Content directly
            dlg.SetBodyContent(stack);

            // Focus and select all text after the dialog is loaded
            dlg.Loaded += (s, e) =>
            {
                messageBox.Focus();
                messageBox.SelectAll();
            };

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