using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnityAgent.Managers
{
    public static class GeminiImagePanel
    {
        public static DockPanel Create(AgentTask task, out RichTextBox outputBox, out WrapPanel imageGallery)
        {
            var root = new DockPanel();

            // Bottom: status/log text box
            outputBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MaxHeight = 120
            };
            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            outputBox.Resources.Add(typeof(Paragraph), paraStyle);

            DockPanel.SetDock(outputBox, Dock.Bottom);
            root.Children.Add(outputBox);

            // Header
            var headerPanel = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            var headerLabel = new TextBlock
            {
                Text = "GEMINI IMAGE LIBRARY",
                Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xA8, 0xDB)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var openFolderBtn = new Button
            {
                Content = "\uE838",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Open images folder",
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 4, 0)
            };
            DockPanel.SetDock(openFolderBtn, Dock.Right);

            headerPanel.Children.Add(openFolderBtn);
            headerPanel.Children.Add(headerLabel);

            DockPanel.SetDock(headerPanel, Dock.Top);
            root.Children.Add(headerPanel);

            // Image gallery (scrollable wrap panel)
            imageGallery = new WrapPanel
            {
                Margin = new Thickness(4)
            };

            var galleryScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = imageGallery
            };

            root.Children.Add(galleryScroll);

            return root;
        }

        public static void AddImage(WrapPanel gallery, string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.DecodePixelWidth = 256;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                var image = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Width = 200,
                    Height = 200,
                    Stretch = Stretch.UniformToFill,
                    Cursor = Cursors.Hand,
                    ToolTip = Path.GetFileName(imagePath),
                    Margin = new Thickness(4)
                };

                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                    Child = image,
                    Margin = new Thickness(4)
                };

                // Click to open the image
                var capturedPath = imagePath;
                border.MouseLeftButtonDown += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(capturedPath) { UseShellExecute = true }); }
                    catch (Exception ex) { AppLogger.Warn("GeminiImagePanel", $"Failed to open image: {capturedPath}", ex); }
                };

                border.MouseEnter += (_, _) =>
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xA8, 0xDB));
                border.MouseLeave += (_, _) =>
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

                gallery.Children.Add(border);
            }
            catch (Exception ex) { AppLogger.Warn("GeminiImagePanel", $"Failed to load image: {imagePath}", ex); }
        }

        public static void SetOpenFolderHandler(DockPanel root, string folderPath)
        {
            // Find the open folder button (first Button in the header DockPanel)
            if (root.Children.Count >= 2 && root.Children[1] is DockPanel header)
            {
                foreach (var child in header.Children)
                {
                    if (child is Button btn && btn.Content?.ToString() == "\uE838")
                    {
                        btn.Click += (_, _) =>
                        {
                            try { Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true }); }
                            catch (Exception ex) { AppLogger.Warn("GeminiImagePanel", $"Failed to open folder: {folderPath}", ex); }
                        };
                        break;
                    }
                }
            }
        }
    }
}
