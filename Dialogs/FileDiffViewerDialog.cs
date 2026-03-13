using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Spritely.Dialogs
{
    /// <summary>
    /// Shows a colored unified diff for a single file in a modal dialog.
    /// </summary>
    public static class FileDiffViewerDialog
    {
        public static void Show(string fileName, string diffContent)
        {
            var dlg = DarkDialogWindow.Create(
                $"Changes — {fileName}",
                850, 600,
                ResizeMode.CanResizeWithGrip);

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

            var greenBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
            var redBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5D, 0x5D));
            var mutedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            greenBrush.Freeze();
            redBrush.Freeze();
            mutedBrush.Freeze();

            var para = new Paragraph();
            foreach (var line in diffContent.Split('\n'))
            {
                Brush brush;
                if (line.StartsWith("@@"))
                    brush = mutedBrush;
                else if (line.StartsWith("+") && !line.StartsWith("+++"))
                    brush = greenBrush;
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                    brush = redBrush;
                else if (line.StartsWith("---") || line.StartsWith("+++") ||
                         line.StartsWith("index ") || line.StartsWith("new file") ||
                         line.StartsWith("deleted file") || line.StartsWith("Binary"))
                    continue;
                else
                    brush = (Brush)Application.Current.FindResource("TextBody");

                para.Inlines.Add(new Run(line + "\n") { Foreground = brush });
            }
            outputBox.Document.Blocks.Add(para);

            var layout = new DockPanel { Margin = new Thickness(12, 4, 12, 12) };
            layout.Children.Add(outputBox);

            dlg.DataContext = layout;
            dlg.ShowDialog();
        }
    }
}
