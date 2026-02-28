using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        private List<ChatMessage> _chatHistory = new();
        private CancellationTokenSource? _chatCts;
        private int _chatBusy; // 0 = idle, 1 = busy; use Interlocked for thread-safe check-and-set

        public ChatManager(
            StackPanel messagesPanel,
            ScrollViewer scrollViewer,
            TextBox input,
            Button sendBtn,
            ComboBox modelCombo,
            ClaudeService claudeService,
            GeminiService geminiService)
        {
            _messagesPanel = messagesPanel;
            _scrollViewer = scrollViewer;
            _input = input;
            _sendBtn = sendBtn;
            _modelCombo = modelCombo;
            _claudeService = claudeService;
            _geminiService = geminiService;
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
            _chatCts?.Cancel();
            Interlocked.Exchange(ref _chatBusy, 0);
            _input.Focus();
        }

        public void HandleSendClick()
        {
            SendChatMessage();
        }

        public void HandleInputKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                SendChatMessage();
            }
        }

        public void HandleModelComboChanged() { }

        public void CancelAndDispose()
        {
            try { _chatCts?.Cancel(); } catch (ObjectDisposedException) { }
            _chatCts?.Dispose();
            _chatCts = null;
        }

        private bool IsChatModelClaude()
        {
            var sel = _modelCombo.SelectedItem as string;
            return sel != null && sel.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        }

        private async void SendChatMessage()
        {
            var text = _input.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

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

            // _chatBusy already set to 1 by CompareExchange above
            _input.Text = "";
            _input.IsEnabled = false;
            _sendBtn.IsEnabled = false;

            var userBubble = AddChatBubble("You", text, (Brush)Application.Current.FindResource("Accent"));

            var senderLabel = useClaude ? "Claude" : "Gemini";
            var responseBubble = AddChatBubble(senderLabel, "", (Brush)Application.Current.FindResource("AccentHover"));
            var responseTextBlock = ((StackPanel)responseBubble.Child).Children[1] as TextBlock;

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
                        _chatHistory, text, onChunk, systemPrompt, _chatCts.Token);
                else
                    response = await _geminiService.SendChatMessageStreamingAsync(
                        _chatHistory, text, onChunk, systemPrompt, _chatCts.Token);

                if (response.StartsWith("[Cancelled]") || response.StartsWith("[Error]"))
                {
                    _messagesPanel.Children.Remove(responseBubble);
                    _messagesPanel.Children.Remove(userBubble);
                    _input.Text = text;
                }
                else
                {
                    _chatHistory.Add(new ChatMessage { Role = "user", Text = text });
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

        private Border AddChatBubble(string sender, string message, Brush accentBrush)
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
