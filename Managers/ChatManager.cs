using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgenticEngine.Managers
{
    public class ChatManager
    {
        private readonly StackPanel _messagesPanel;
        private readonly ScrollViewer _scrollViewer;
        private readonly TextBox _input;
        private readonly Button _sendBtn;
        private readonly ComboBox _modelCombo;
        private readonly ClaudeService _claudeService;
        private readonly GeminiService _geminiService;
        private readonly WrapPanel _imagePreviewPanel;
        private readonly string _imageDir;

        private List<ChatMessage> _chatHistory = new();
        private readonly List<string> _pendingImages = new();
        private CancellationTokenSource? _chatCts;
        private int _chatBusy; // 0 = idle, 1 = busy; use Interlocked for thread-safe check-and-set

        public ChatManager(
            StackPanel messagesPanel,
            ScrollViewer scrollViewer,
            TextBox input,
            Button sendBtn,
            ComboBox modelCombo,
            ClaudeService claudeService,
            GeminiService geminiService,
            WrapPanel imagePreviewPanel,
            string appDataDir)
        {
            _messagesPanel = messagesPanel;
            _scrollViewer = scrollViewer;
            _input = input;
            _sendBtn = sendBtn;
            _modelCombo = modelCombo;
            _claudeService = claudeService;
            _geminiService = geminiService;
            _imagePreviewPanel = imagePreviewPanel;
            _imageDir = Path.Combine(appDataDir, "chat_images");
            Directory.CreateDirectory(_imageDir);
        }

        public void PopulateModelCombo()
        {
            var prev = _modelCombo.SelectedItem as string;
            _modelCombo.Items.Clear();

            if (_geminiService.IsConfigured)
                foreach (var m in GeminiService.AvailableModels)
                    _modelCombo.Items.Add(m);

            if (_claudeService.IsConfigured)
                foreach (var m in ClaudeService.AvailableModels)
                    _modelCombo.Items.Add(m);

            if (_modelCombo.Items.Count == 0)
            {
                _modelCombo.Items.Add("(no API key set)");
                _modelCombo.SelectedIndex = 0;
                return;
            }

            if (prev != null && _modelCombo.Items.Contains(prev))
                _modelCombo.SelectedItem = prev;
            else
                _modelCombo.SelectedIndex = 0;
        }

        public void HandleNewChat()
        {
            _chatHistory.Clear();
            _messagesPanel.Children.Clear();
            ClearPendingImages();
            _chatCts?.Cancel();
            Interlocked.Exchange(ref _chatBusy, 0);
            _input.Focus();
        }

        public void HandleSendClick()
        {
            SendChatMessage();
        }

        public void HandleInputKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SendChatMessage();
            }
        }

        public bool HandlePaste()
        {
            if (Clipboard.ContainsImage())
            {
                try
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        var fileName = $"chat_paste_{DateTime.Now:yyyyMMdd_HHmmss}_{_pendingImages.Count}.png";
                        var filePath = Path.Combine(_imageDir, fileName);
                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(image));
                            encoder.Save(stream);
                        }
                        AddPendingImage(filePath);
                        return true;
                    }
                }
                catch (Exception ex) { AppLogger.Warn("ChatManager", "Failed to paste image", ex); }
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var added = false;
                foreach (string? file in files)
                {
                    if (file != null && TaskLauncher.IsImageFile(file))
                    {
                        AddPendingImage(file);
                        added = true;
                    }
                }
                return added;
            }

            return false;
        }

        public bool HandleDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Any(TaskLauncher.IsImageFile))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                    return true;
                }
            }
            return false;
        }

        public bool HandleDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    var added = false;
                    foreach (var file in files)
                    {
                        if (TaskLauncher.IsImageFile(file))
                        {
                            AddPendingImage(file);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        e.Handled = true;
                        return true;
                    }
                }
            }
            return false;
        }

        public void HandleModelComboChanged() { }

        public void CancelAndDispose()
        {
            try { _chatCts?.Cancel(); } catch (ObjectDisposedException) { }
            _chatCts?.Dispose();
            _chatCts = null;
        }

        private void AddPendingImage(string path)
        {
            _pendingImages.Add(path);
            AddImagePreviewThumbnail(path);
            _imagePreviewPanel.Visibility = Visibility.Visible;
        }

        private void AddImagePreviewThumbnail(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelHeight = 48;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var img = new Image
            {
                Source = bitmap,
                Width = 48,
                Height = 48,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = Path.GetFileName(path),
                Cursor = Cursors.Hand
            };

            var border = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = img
            };

            // Click to remove
            var imagePath = path;
            border.MouseLeftButtonUp += (_, _) =>
            {
                _pendingImages.Remove(imagePath);
                _imagePreviewPanel.Children.Remove(border);
                if (_pendingImages.Count == 0)
                    _imagePreviewPanel.Visibility = Visibility.Collapsed;
            };

            _imagePreviewPanel.Children.Add(border);
        }

        private void ClearPendingImages()
        {
            _pendingImages.Clear();
            _imagePreviewPanel.Children.Clear();
            _imagePreviewPanel.Visibility = Visibility.Collapsed;
        }

        private bool IsChatModelClaude()
        {
            var sel = _modelCombo.SelectedItem as string;
            return sel != null && sel.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        }

        private async void SendChatMessage()
        {
            var text = _input.Text?.Trim();
            if (string.IsNullOrEmpty(text) && _pendingImages.Count == 0) return;

            // Atomic check-and-set: only proceed if we transition from 0 (idle) to 1 (busy)
            if (Interlocked.CompareExchange(ref _chatBusy, 1, 0) != 0) return;

            var selectedModel = _modelCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedModel) || selectedModel == "(no API key set)")
            {
                AddChatBubble("System", "No chat model available.\nConfigure a Gemini or Claude API key in Settings.",
                    (Brush)Application.Current.FindResource("DangerAlert"));
                return;
            }

            bool useClaude = IsChatModelClaude();

            if (useClaude && !_claudeService.IsConfigured)
            {
                AddChatBubble("System", "Claude API key not configured.\nSet it in Settings > Claude.",
                    (Brush)Application.Current.FindResource("DangerAlert"));
                return;
            }
            if (!useClaude && !_geminiService.IsConfigured)
            {
                AddChatBubble("System", "Gemini API key not configured.\nSet it in Settings > Gemini.",
                    (Brush)Application.Current.FindResource("DangerAlert"));
                return;
            }

            // Capture and clear pending images
            List<string>? sentImages = _pendingImages.Count > 0 ? new List<string>(_pendingImages) : null;
            ClearPendingImages();

            // _chatBusy already set to 1 by CompareExchange above
            _input.Text = "";
            _input.IsEnabled = false;
            _sendBtn.IsEnabled = false;

            var userBubble = AddChatBubble("You", text ?? "", (Brush)Application.Current.FindResource("Accent"), sentImages);

            var senderLabel = useClaude ? "Claude" : "Gemini";
            var responseBubble = AddChatBubble(senderLabel, "", (Brush)Application.Current.FindResource("AccentHover"));
            var responseTextBlock = FindTextBlock(responseBubble);

            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();

            try
            {
                var systemPrompt = "You are a helpful coding assistant embedded in the Agentic Engine app. " +
                    "Give concise, practical suggestions. Keep responses short unless asked for detail. " +
                    "The user is working on software projects, primarily Unity game development.";

                if (useClaude)
                    _claudeService.SelectedModel = selectedModel;
                else
                    _geminiService.SelectedModel = selectedModel;

                Action<string> onChunk = chunk =>
                {
                    responseTextBlock!.Text += chunk;
                    _scrollViewer.ScrollToEnd();
                };

                string response;
                if (useClaude)
                    response = await _claudeService.SendChatMessageStreamingAsync(
                        _chatHistory, text ?? "", onChunk, systemPrompt, _chatCts.Token);
                else
                    response = await _geminiService.SendChatMessageStreamingAsync(
                        _chatHistory, text ?? "", onChunk, systemPrompt, _chatCts.Token);

                if (response.StartsWith("[Cancelled]") || response.StartsWith("[Error]"))
                {
                    _messagesPanel.Children.Remove(responseBubble);
                    _messagesPanel.Children.Remove(userBubble);
                    _input.Text = text;
                }
                else
                {
                    _chatHistory.Add(new ChatMessage { Role = "user", Text = text ?? "", ImagePaths = sentImages });
                    _chatHistory.Add(new ChatMessage { Role = "model", Text = response });
                }
            }
            catch (OperationCanceledException)
            {
                _messagesPanel.Children.Remove(responseBubble);
                _messagesPanel.Children.Remove(userBubble);
                _input.Text = text;
            }
            catch (Exception)
            {
                _messagesPanel.Children.Remove(responseBubble);
                _messagesPanel.Children.Remove(userBubble);
                _input.Text = text;
            }
            finally
            {
                Interlocked.Exchange(ref _chatBusy, 0);
                _input.IsEnabled = true;
                _sendBtn.IsEnabled = true;
                _input.Focus();
            }
        }

        private static TextBlock? FindTextBlock(Border bubble)
        {
            var panel = bubble.Child as StackPanel;
            if (panel == null) return null;
            for (int i = panel.Children.Count - 1; i >= 0; i--)
            {
                if (panel.Children[i] is TextBlock tb && tb.FontSize == 12)
                    return tb;
            }
            return null;
        }

        private Border AddChatBubble(string sender, string message, Brush accentBrush, List<string>? imagePaths = null)
        {
            bool isUser = sender == "You";

            var bubble = new Border
            {
                Background = (Brush)Application.Current.FindResource(isUser ? "BgCard" : "BgSection"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(isUser ? 20 : 0, 2, isUser ? 0 : 20, 2),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = sender,
                Foreground = accentBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 2)
            });

            // Display image thumbnails if present
            if (imagePaths != null && imagePaths.Count > 0)
            {
                var imageWrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
                foreach (var imgPath in imagePaths)
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(imgPath, UriKind.Absolute);
                        bitmap.DecodePixelWidth = 160;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        var img = new Image
                        {
                            Source = bitmap,
                            MaxWidth = 160,
                            MaxHeight = 120,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 0, 4, 4),
                            ToolTip = Path.GetFileName(imgPath),
                            Cursor = Cursors.Hand
                        };

                        var imgBorder = new Border
                        {
                            BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = img
                        };

                        // Click to open image in default viewer
                        var path = imgPath;
                        imgBorder.MouseLeftButtonUp += (_, _) =>
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = path,
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                        };

                        imageWrap.Children.Add(imgBorder);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("ChatManager", $"Failed to load image thumbnail: {ex.Message}");
                    }
                }
                panel.Children.Add(imageWrap);
            }

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = (Brush)Application.Current.FindResource("TextBright"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(msgBlock);

            bubble.Child = panel;

            var contextMenu = new ContextMenu();
            var copyItem = new MenuItem { Header = "Copy" };
            copyItem.Click += (_, _) => Clipboard.SetText(msgBlock.Text);
            contextMenu.Items.Add(copyItem);
            bubble.ContextMenu = contextMenu;

            _messagesPanel.Children.Add(bubble);
            _scrollViewer.ScrollToEnd();
            return bubble;
        }
    }
}
