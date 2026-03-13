using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Spritely.Helpers;
using Spritely.Managers;

namespace Spritely.Dialogs
{
    /// <summary>
    /// Paginated viewer for full task output stored on disk by StreamingOutputWriter.
    /// Loads one page at a time to avoid pulling the entire log into memory.
    /// </summary>
    public static class FullOutputViewerDialog
    {
        private const int LinesPerPage = 500;

        public static void Show(string taskId, string description, StreamingOutputWriter outputWriter)
        {
            if (!outputWriter.HasLogFile(taskId))
            {
                DarkDialog.ShowAlert("No output log file found for this task.", "Full Output");
                return;
            }

            var dlg = DarkDialogWindow.Create(
                $"Full Output — {description}",
                900, 650,
                ResizeMode.CanResizeWithGrip);

            int currentPage = 0;
            int totalLines = 0;

            var outputBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = (Brush)Application.Current.FindResource("BgAbyss"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };
            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            outputBox.Resources.Add(typeof(Paragraph), paraStyle);

            var pageInfo = new TextBlock
            {
                Foreground = BrushCache.Theme("TextSubtle"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };

            var prevBtn = new Button
            {
                Content = "\uE76B Prev",
                FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI"),
                Style = (Style)Application.Current.FindResource("Btn"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 4, 0)
            };
            var nextBtn = new Button
            {
                Content = "Next \uE76C",
                FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI"),
                Style = (Style)Application.Current.FindResource("Btn"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 0, 0, 0)
            };
            var jumpToEndBtn = new Button
            {
                Content = "Jump to End",
                Style = (Style)Application.Current.FindResource("Btn"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0)
            };

            var sizeInfo = new TextBlock
            {
                Foreground = BrushCache.Theme("TextSubtle"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            void LoadPage(int page)
            {
                var lines = outputWriter.ReadPage(taskId, page, LinesPerPage, out totalLines);
                int totalPages = Math.Max(1, (totalLines + LinesPerPage - 1) / LinesPerPage);
                currentPage = Math.Clamp(page, 0, totalPages - 1);

                outputBox.Document.Blocks.Clear();
                if (lines.Count > 0)
                {
                    var para = new Paragraph();
                    para.Inlines.Add(new Run(string.Join("\n", lines)));
                    outputBox.Document.Blocks.Add(para);
                }

                int displayPage = currentPage + 1;
                pageInfo.Text = $"Page {displayPage} / {totalPages}  ({totalLines:N0} lines)";
                prevBtn.IsEnabled = currentPage > 0;
                nextBtn.IsEnabled = currentPage < totalPages - 1;

                long bytes = outputWriter.GetBytesWritten(taskId);
                sizeInfo.Text = bytes > 0 ? $"{bytes / 1024.0:F1} KB on disk" : "";
            }

            prevBtn.Click += (_, _) => LoadPage(currentPage - 1);
            nextBtn.Click += (_, _) => LoadPage(currentPage + 1);
            jumpToEndBtn.Click += (_, _) =>
            {
                // Read to get total, then jump to last page
                outputWriter.ReadPage(taskId, 0, LinesPerPage, out int total);
                int lastPage = Math.Max(0, (total + LinesPerPage - 1) / LinesPerPage - 1);
                LoadPage(lastPage);
            };

            var navPanel = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
            var leftNav = new StackPanel { Orientation = Orientation.Horizontal };
            leftNav.Children.Add(prevBtn);
            leftNav.Children.Add(pageInfo);
            leftNav.Children.Add(nextBtn);
            leftNav.Children.Add(jumpToEndBtn);
            DockPanel.SetDock(sizeInfo, Dock.Right);
            navPanel.Children.Add(sizeInfo);
            navPanel.Children.Add(leftNav);

            var layout = new DockPanel { Margin = new Thickness(12, 4, 12, 12) };
            DockPanel.SetDock(navPanel, Dock.Bottom);
            layout.Children.Add(navPanel);
            layout.Children.Add(outputBox);

            dlg.DataContext = layout;

            // Keyboard navigation
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Left && prevBtn.IsEnabled) LoadPage(currentPage - 1);
                else if (ke.Key == Key.Right && nextBtn.IsEnabled) LoadPage(currentPage + 1);
                else if (ke.Key == Key.Home) LoadPage(0);
                else if (ke.Key == Key.End)
                {
                    outputWriter.ReadPage(taskId, 0, LinesPerPage, out int total);
                    LoadPage(Math.Max(0, (total + LinesPerPage - 1) / LinesPerPage - 1));
                }
            };

            // Load last page by default (most relevant)
            outputWriter.ReadPage(taskId, 0, LinesPerPage, out int initTotal);
            int initLastPage = Math.Max(0, (initTotal + LinesPerPage - 1) / LinesPerPage - 1);
            LoadPage(initLastPage);

            dlg.ShowDialog();
        }
    }
}
