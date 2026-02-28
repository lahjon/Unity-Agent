using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgenticEngine.Models;

namespace AgenticEngine
{
    public partial class MainWindow
    {
        // ── Task Templates ────────────────────────────────────────

        private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
        {
            var name = Dialogs.DarkDialog.ShowTextInput("Save as Template", "Template name:");
            if (string.IsNullOrEmpty(name)) return;

            var modelTag = "ClaudeCode";
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
                modelTag = modelItem.Tag?.ToString() ?? "ClaudeCode";

            var template = new TaskTemplate
            {
                Name = name,
                Description = name,
                AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "",
                RemoteSession = RemoteSessionToggle.IsChecked == true,
                Headless = HeadlessToggle.IsChecked == true,
                SpawnTeam = SpawnTeamToggle.IsChecked == true,
                IsOvernight = OvernightToggle.IsChecked == true,
                ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true,
                PlanOnly = PlanOnlyToggle.IsChecked == true,
                IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true,
                UseMcp = UseMcpToggle.IsChecked == true,
                NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true,
                AutoDecompose = AutoDecomposeToggle.IsChecked == true,
                Model = modelTag,
            };

            _settingsManager.TaskTemplates.Insert(0, template);
            _settingsManager.SaveTemplates();
            RenderTemplateCombo();
        }

        private void RenderTemplateCombo()
        {
            TemplateCombo.SelectionChanged -= TemplateCombo_Changed;
            TemplateCombo.Items.Clear();
            TemplateCombo.Items.Add(new ComboBoxItem { Content = "(No Template)", Tag = "" });

            foreach (var t in _settingsManager.TaskTemplates)
            {
                var item = new ComboBoxItem { Content = t.Name, Tag = t.Id };
                item.ToolTip = BuildTemplateTooltip(t);
                TemplateCombo.Items.Add(item);
            }

            // Add a "Manage..." option at the end
            TemplateCombo.Items.Add(new ComboBoxItem
            {
                Content = "Manage templates...",
                Tag = "__manage__",
                Foreground = (Brush)FindResource("TextMuted"),
                FontStyle = FontStyles.Italic
            });

            TemplateCombo.SelectedIndex = 0;
            TemplateCombo.SelectionChanged += TemplateCombo_Changed;
        }

        private static string BuildTemplateTooltip(TaskTemplate t)
        {
            var flags = new List<string>();
            if (t.RemoteSession) flags.Add("Remote");
            if (t.Headless) flags.Add("Headless");
            if (t.SpawnTeam) flags.Add("Team");
            if (t.IsOvernight) flags.Add("Overnight");
            if (t.ExtendedPlanning) flags.Add("ExtPlanning");
            if (t.PlanOnly) flags.Add("PlanOnly");
            if (t.AutoDecompose) flags.Add("AutoDecompose");
            if (t.NoGitWrite) flags.Add("NoGitWrite");
            if (t.IgnoreFileLocks) flags.Add("IgnoreLocks");
            if (t.UseMcp) flags.Add("MCP");
            var tooltip = flags.Count > 0 ? string.Join(", ", flags) : "(default settings)";
            if (!string.IsNullOrWhiteSpace(t.AdditionalInstructions))
                tooltip += "\n\nInstructions: " + (t.AdditionalInstructions.Length > 80
                    ? t.AdditionalInstructions[..80] + "..."
                    : t.AdditionalInstructions);
            return tooltip;
        }

        private void TemplateCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (TemplateCombo.SelectedItem is not ComboBoxItem selected) return;
            var tag = selected.Tag?.ToString();

            if (tag == "__manage__")
            {
                // Show template management
                TemplateCombo.SelectionChanged -= TemplateCombo_Changed;
                TemplateCombo.SelectedIndex = 0;
                TemplateCombo.SelectionChanged += TemplateCombo_Changed;
                ShowTemplateManagement();
                return;
            }

            if (string.IsNullOrEmpty(tag))
            {
                // Reset all controls to defaults
                ResetToNoTemplate();
                return;
            }

            var template = _settingsManager.TaskTemplates.Find(t => t.Id == tag);
            if (template == null) return;

            ApplyTemplate(template);
        }

        private void ApplyTemplate(TaskTemplate template)
        {
            RemoteSessionToggle.IsChecked = template.RemoteSession;
            HeadlessToggle.IsChecked = template.Headless;
            SpawnTeamToggle.IsChecked = template.SpawnTeam;
            OvernightToggle.IsChecked = template.IsOvernight;
            ExtendedPlanningToggle.IsChecked = template.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = template.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = template.IgnoreFileLocks;
            UseMcpToggle.IsChecked = template.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = template.NoGitWrite;
            AutoDecomposeToggle.IsChecked = template.AutoDecompose;

            AdditionalInstructionsInput.Text = template.AdditionalInstructions ?? "";

            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (ModelCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == template.Model)
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            UpdateExecuteButtonText();
        }

        private void ResetToNoTemplate()
        {
            if (RemoteSessionToggle == null) return; // Called during InitializeComponent before controls exist
            RemoteSessionToggle.IsChecked = false;
            HeadlessToggle.IsChecked = false;
            SpawnTeamToggle.IsChecked = false;
            OvernightToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;
            IgnoreFileLocksToggle.IsChecked = true;
            UseMcpToggle.IsChecked = false;
            DefaultNoGitWriteToggle.IsChecked = true;
            AutoDecomposeToggle.IsChecked = false;

            AdditionalInstructionsInput.Text = "";
            ModelCombo.SelectedIndex = 0;

            UpdateExecuteButtonText();
        }

        private void ShowTemplateManagement()
        {
            if (_settingsManager.TaskTemplates.Count == 0)
            {
                Dialogs.DarkDialog.ShowAlert("No templates saved yet.", "Task Templates");
                return;
            }

            var dlg = new Window
            {
                Width = 720,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
            };

            var outerBorder = new Border
            {
                Background = (Brush)FindResource("BgSurface"),
                BorderBrush = (Brush)FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
            };

            var root = new DockPanel();
            outerBorder.Child = root;

            // Title bar
            var titleBar = new DockPanel { Margin = new Thickness(20, 16, 20, 0) };
            titleBar.MouseLeftButtonDown += (_, _) => dlg.DragMove();

            var closeBtn = new Button
            {
                Content = "\u2715",
                Style = (Style)FindResource("Btn"),
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 14,
                Padding = new Thickness(8, 4, 8, 4),
            };
            closeBtn.Click += (_, _) => dlg.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);

            var titleText = new TextBlock
            {
                Text = "Manage Templates",
                Foreground = (Brush)FindResource("Accent"),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleBar.Children.Add(titleText);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            // Template count
            var countText = new TextBlock
            {
                Text = $"{_settingsManager.TaskTemplates.Count} template(s)",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(20, 6, 20, 0),
            };
            DockPanel.SetDock(countText, Dock.Top);
            root.Children.Add(countText);

            // Scrollable template list
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(20, 10, 20, 20),
            };
            var stack = new StackPanel();
            scroll.Content = stack;
            root.Children.Add(scroll);

            for (int i = 0; i < _settingsManager.TaskTemplates.Count; i++)
            {
                var t = _settingsManager.TaskTemplates[i];
                stack.Children.Add(BuildTemplateCard(t, dlg, countText, stack));
            }

            dlg.Content = outerBorder;
            dlg.KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Escape) dlg.Close();
            };
            dlg.ShowDialog();
        }

        private Border BuildTemplateCard(TaskTemplate t, Window dlg, TextBlock countText, StackPanel stack)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("BgElevated"),
                BorderBrush = (Brush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
            };

            var content = new StackPanel();
            card.Child = content;

            // Row 1: Name
            var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            nameRow.Children.Add(new TextBlock
            {
                Text = "NAME",
                Foreground = (Brush)FindResource("TextMuted"),
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
            });
            var nameBox = new TextBox
            {
                Text = t.Name,
                Background = (Brush)FindResource("BgDeep"),
                Foreground = (Brush)FindResource("TextPrimary"),
                CaretBrush = (Brush)FindResource("TextPrimary"),
                BorderBrush = (Brush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3),
            };
            nameRow.Children.Add(nameBox);
            content.Children.Add(nameRow);

            // Row 2: Description
            var descRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            descRow.Children.Add(new TextBlock
            {
                Text = "DESC",
                Foreground = (Brush)FindResource("TextMuted"),
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
            });
            var descBox = new TextBox
            {
                Text = t.Description,
                Background = (Brush)FindResource("BgDeep"),
                Foreground = (Brush)FindResource("TextPrimary"),
                CaretBrush = (Brush)FindResource("TextPrimary"),
                BorderBrush = (Brush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3),
            };
            descRow.Children.Add(descBox);
            content.Children.Add(descRow);

            // Row 3: Toggles
            content.Children.Add(new TextBlock
            {
                Text = "FLAGS",
                Foreground = (Brush)FindResource("TextMuted"),
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            var togglePanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remoteChk = MakeTemplateCheckBox("Remote", t.RemoteSession);
            var headlessChk = MakeTemplateCheckBox("Headless", t.Headless);
            var teamChk = MakeTemplateCheckBox("Team", t.SpawnTeam);
            var overnightChk = MakeTemplateCheckBox("Overnight", t.IsOvernight);
            var extPlanChk = MakeTemplateCheckBox("ExtPlanning", t.ExtendedPlanning);
            var planOnlyChk = MakeTemplateCheckBox("PlanOnly", t.PlanOnly);
            var ignoreLockChk = MakeTemplateCheckBox("IgnoreLocks", t.IgnoreFileLocks);
            var mcpChk = MakeTemplateCheckBox("MCP", t.UseMcp);
            var noGitChk = MakeTemplateCheckBox("NoGitWrite", t.NoGitWrite);
            var msgBusChk = MakeTemplateCheckBox("MsgBus", t.UseMessageBus);
            var autoDecompChk = MakeTemplateCheckBox("AutoDecompose", t.AutoDecompose);

            togglePanel.Children.Add(remoteChk);
            togglePanel.Children.Add(headlessChk);
            togglePanel.Children.Add(teamChk);
            togglePanel.Children.Add(overnightChk);
            togglePanel.Children.Add(extPlanChk);
            togglePanel.Children.Add(planOnlyChk);
            togglePanel.Children.Add(ignoreLockChk);
            togglePanel.Children.Add(mcpChk);
            togglePanel.Children.Add(noGitChk);
            togglePanel.Children.Add(msgBusChk);
            togglePanel.Children.Add(autoDecompChk);
            content.Children.Add(togglePanel);

            // Row 4: Additional Instructions
            content.Children.Add(new TextBlock
            {
                Text = "ADDITIONAL INSTRUCTIONS",
                Foreground = (Brush)FindResource("TextMuted"),
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            var instrBox = new TextBox
            {
                Text = t.AdditionalInstructions ?? "",
                Background = (Brush)FindResource("BgDeep"),
                Foreground = (Brush)FindResource("TextPrimary"),
                CaretBrush = (Brush)FindResource("TextPrimary"),
                BorderBrush = (Brush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(6),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 32,
                MaxHeight = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10),
            };
            content.Children.Add(instrBox);

            // Row 5: Action buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var editBtn = new Button
            {
                Content = "Edit",
                Style = (Style)FindResource("SecondaryBtn"),
                FontSize = 11,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Load this template's values into the main UI",
            };
            editBtn.Click += (_, _) =>
            {
                ApplyTemplate(t);
                dlg.Close();
            };

            var saveBtn = new Button
            {
                Content = "Save Changes",
                Style = (Style)FindResource("Btn"),
                Background = (Brush)FindResource("Accent"),
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Save modifications to this template",
            };
            saveBtn.Click += (_, _) =>
            {
                t.Name = nameBox.Text?.Trim() ?? "";
                t.Description = descBox.Text?.Trim() ?? "";
                t.RemoteSession = remoteChk.IsChecked == true;
                t.Headless = headlessChk.IsChecked == true;
                t.SpawnTeam = teamChk.IsChecked == true;
                t.IsOvernight = overnightChk.IsChecked == true;
                t.ExtendedPlanning = extPlanChk.IsChecked == true;
                t.PlanOnly = planOnlyChk.IsChecked == true;
                t.IgnoreFileLocks = ignoreLockChk.IsChecked == true;
                t.UseMcp = mcpChk.IsChecked == true;
                t.NoGitWrite = noGitChk.IsChecked == true;
                t.UseMessageBus = msgBusChk.IsChecked == true;
                t.AutoDecompose = autoDecompChk.IsChecked == true;
                t.AdditionalInstructions = instrBox.Text?.Trim() ?? "";

                _settingsManager.SaveTemplates();
                RenderTemplateCombo();

                // Brief visual feedback
                saveBtn.Content = "Saved \u2713";
                saveBtn.IsEnabled = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) =>
                {
                    saveBtn.Content = "Save Changes";
                    saveBtn.IsEnabled = true;
                    timer.Stop();
                };
                timer.Start();
            };

            var deleteBtn = new Button
            {
                Content = "Delete",
                Style = (Style)FindResource("DangerBtn"),
                FontSize = 11,
                Padding = new Thickness(12, 5, 12, 5),
                ToolTip = "Delete this template permanently",
            };
            deleteBtn.Click += (_, _) =>
            {
                _settingsManager.TaskTemplates.Remove(t);
                _settingsManager.SaveTemplates();
                RenderTemplateCombo();

                stack.Children.Remove(card);
                countText.Text = $"{_settingsManager.TaskTemplates.Count} template(s)";

                if (_settingsManager.TaskTemplates.Count == 0)
                    dlg.Close();
            };

            btnRow.Children.Add(editBtn);
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(deleteBtn);
            content.Children.Add(btnRow);

            return card;
        }

        private CheckBox MakeTemplateCheckBox(string label, bool isChecked)
        {
            return new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = (Brush)FindResource("TextBody"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 14, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
        }
    }
}
