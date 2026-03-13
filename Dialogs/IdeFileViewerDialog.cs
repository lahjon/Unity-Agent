using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Spritely.Helpers;

namespace Spritely.Dialogs
{
    /// <summary>
    /// Shows a file's content with basic syntax coloring in a modal dialog.
    /// Supports viewing source code with line numbers.
    /// </summary>
    public static class IdeFileViewerDialog
    {
        public static void Show(string filePath)
        {
            if (!File.Exists(filePath))
            {
                DarkDialog.ShowAlert($"File not found:\n{filePath}", "Open File");
                return;
            }

            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Failed to read file:\n{ex.Message}", "Open File");
                return;
            }

            var dlg = DarkDialogWindow.Create(
                $"{fileName}",
                950, 700,
                ResizeMode.CanResizeWithGrip);

            // Add file path to title bar
            var pathLabel = new TextBlock
            {
                Text = $"  {filePath}",
                Foreground = BrushCache.Theme("TextDim"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 500
            };
            dlg.TitleBarPanel.Children.Add(pathLabel);
            dlg.TitleTextBlock.MaxWidth = 300;
            dlg.TitleTextBlock.TextTrimming = TextTrimming.CharacterEllipsis;

            var layout = new DockPanel { Margin = new Thickness(0) };

            // Top toolbar
            var toolbar = BuildToolbar(filePath, content);
            DockPanel.SetDock(toolbar, Dock.Top);
            layout.Children.Add(toolbar);

            // Content area with line numbers
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushCache.Get("#0A0A0A")
            };

            // Line numbers
            var lineNumberBox = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = BrushCache.Get("#4A4A4A"),
                Background = BrushCache.Get("#0E0E0E"),
                Padding = new Thickness(8, 8, 12, 8),
                TextAlignment = TextAlignment.Right
            };

            // Code content
            var codeBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = BrushCache.Get("#0A0A0A"),
                Foreground = BrushCache.Theme("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };

            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            codeBox.Resources.Add(typeof(Paragraph), paraStyle);

            // Render content with syntax coloring
            RenderFileContent(codeBox, lineNumberBox, content, extension);

            // Sync scrolling
            var lineScroll = new ScrollViewer
            {
                Content = lineNumberBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = BrushCache.Get("#0E0E0E")
            };

            var codeScroll = new ScrollViewer
            {
                Content = codeBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushCache.Get("#0A0A0A")
            };

            codeScroll.ScrollChanged += (_, e) =>
            {
                lineScroll.ScrollToVerticalOffset(e.VerticalOffset);
            };

            Grid.SetColumn(lineScroll, 0);
            Grid.SetColumn(codeScroll, 1);
            contentGrid.Children.Add(lineScroll);
            contentGrid.Children.Add(codeScroll);

            layout.Children.Add(contentGrid);

            dlg.SetBodyContent(layout);
            dlg.ShowDialog();
        }

        private static Border BuildToolbar(string filePath, string content)
        {
            var toolbar = new WrapPanel { Margin = new Thickness(4) };

            // File info
            var lines = content.Split('\n').Length;
            var sizeKb = new FileInfo(filePath).Length / 1024.0;
            var info = new TextBlock
            {
                Text = $"{lines} lines  \u2022  {sizeKb:F1} KB  \u2022  {Path.GetExtension(filePath)}",
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0)
            };
            toolbar.Children.Add(info);

            // Open in editor button
            var openBtn = new Button
            {
                Content = "\uE7AC",
                ToolTip = "Open in default editor",
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("StatusOrange"),
                Margin = new Thickness(0, 0, 4, 0)
            };
            openBtn.Click += (_, _) => OpenFileExternal(filePath);
            toolbar.Children.Add(openBtn);

            // Show in explorer button
            var explorerBtn = new Button
            {
                Content = "\uEC50",
                ToolTip = "Show in Explorer",
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("StatusOrange"),
                Margin = new Thickness(0, 0, 4, 0)
            };
            explorerBtn.Click += (_, _) => ShowInExplorer(filePath);
            toolbar.Children.Add(explorerBtn);

            // Copy path button
            var copyBtn = new Button
            {
                Content = "\uE8C8",
                ToolTip = "Copy file path",
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("StatusOrange"),
                Margin = new Thickness(0, 0, 4, 0)
            };
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(filePath); }
                catch { /* clipboard access can fail */ }
            };
            toolbar.Children.Add(copyBtn);

            return new Border
            {
                Background = BrushCache.Get("#141414"),
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 4, 4, 4),
                Child = toolbar
            };
        }

        private static void RenderFileContent(RichTextBox codeBox, TextBlock lineNumberBox, string content, string extension)
        {
            var lines = content.Split('\n');
            var para = new Paragraph();
            var lineNumBuilder = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                lineNumBuilder.AppendLine((i + 1).ToString());
                var line = lines[i].TrimEnd('\r');
                var coloredRun = ColorLine(line, extension);
                para.Inlines.Add(coloredRun);
                if (i < lines.Length - 1)
                    para.Inlines.Add(new Run("\n") { Foreground = BrushCache.Theme("TextBody") });
            }

            lineNumberBox.Text = lineNumBuilder.ToString().TrimEnd();
            codeBox.Document.Blocks.Clear();
            codeBox.Document.Blocks.Add(para);
        }

        private static Run ColorLine(string line, string extension)
        {
            var trimmed = line.TrimStart();
            Brush brush;

            if (extension is "cs")
                brush = GetCSharpColor(trimmed);
            else if (extension is "xaml" or "xml")
                brush = GetXmlColor(trimmed);
            else if (extension is "json")
                brush = GetJsonColor(trimmed);
            else
                brush = BrushCache.Theme("TextBody");

            return new Run(line) { Foreground = brush };
        }

        private static Brush GetCSharpColor(string trimmed)
        {
            // Comments
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                return BrushCache.Get("#6A9955");

            // Using/namespace
            if (trimmed.StartsWith("using ") || trimmed.StartsWith("namespace "))
                return BrushCache.Get("#C586C0");

            // Keywords
            if (trimmed.StartsWith("public ") || trimmed.StartsWith("private ") ||
                trimmed.StartsWith("protected ") || trimmed.StartsWith("internal ") ||
                trimmed.StartsWith("static ") || trimmed.StartsWith("class ") ||
                trimmed.StartsWith("interface ") || trimmed.StartsWith("enum ") ||
                trimmed.StartsWith("struct ") || trimmed.StartsWith("abstract ") ||
                trimmed.StartsWith("virtual ") || trimmed.StartsWith("override ") ||
                trimmed.StartsWith("async ") || trimmed.StartsWith("await ") ||
                trimmed.StartsWith("return ") || trimmed.StartsWith("if ") ||
                trimmed.StartsWith("else") || trimmed.StartsWith("for ") ||
                trimmed.StartsWith("foreach ") || trimmed.StartsWith("while ") ||
                trimmed.StartsWith("switch ") || trimmed.StartsWith("case ") ||
                trimmed.StartsWith("try") || trimmed.StartsWith("catch") ||
                trimmed.StartsWith("finally") || trimmed.StartsWith("throw ") ||
                trimmed.StartsWith("new ") || trimmed.StartsWith("var "))
                return BrushCache.Get("#569CD6");

            // Attributes
            if (trimmed.StartsWith("[") && trimmed.Contains("]"))
                return BrushCache.Get("#4EC9B0");

            // String literals
            if (trimmed.Contains("\""))
                return BrushCache.Get("#CE9178");

            // Braces/brackets
            if (trimmed is "{" or "}" or "};")
                return BrushCache.Get("#808080");

            return BrushCache.Theme("TextBody");
        }

        private static Brush GetXmlColor(string trimmed)
        {
            if (trimmed.StartsWith("<!--"))
                return BrushCache.Get("#6A9955");
            if (trimmed.StartsWith("<") || trimmed.StartsWith("/>") || trimmed.StartsWith("</"))
                return BrushCache.Get("#569CD6");
            if (trimmed.Contains("=\""))
                return BrushCache.Get("#9CDCFE");
            return BrushCache.Theme("TextBody");
        }

        private static Brush GetJsonColor(string trimmed)
        {
            if (trimmed.StartsWith("\"") && trimmed.Contains(":"))
                return BrushCache.Get("#9CDCFE");
            if (trimmed.Contains("\""))
                return BrushCache.Get("#CE9178");
            if (trimmed is "{" or "}" or "[" or "]" or "}," or "],")
                return BrushCache.Get("#808080");
            return BrushCache.Theme("TextBody");
        }

        public static void OpenFileExternal(string filePath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Failed to open file:\n{ex.Message}", "Open File");
            }
        }

        public static void ShowInExplorer(string filePath)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Failed to open Explorer:\n{ex.Message}", "Show in Explorer");
            }
        }
    }
}
