using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HappyEngine.Managers;
using HappyEngine.Models;

namespace HappyEngine.Dialogs
{
    public static class ProjectSettingsDialog
    {
        public static void Show(ProjectEntry entry, Action saveProjects, Action? onMcpVisibilityChanged = null)
        {
            var dlg = DarkDialogWindow.Create($"Project Settings \u2014 {entry.DisplayName}", 520, 600,
                resizeMode: ResizeMode.CanResizeWithGrip);
            dlg.SizeToContent = SizeToContent.Manual;
            dlg.MinHeight = 400;
            dlg.MinWidth = 420;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(20, 8, 20, 20)
            };

            var stack = new StackPanel { MaxWidth = 460 };

            // ── System Prompt ──
            AddSectionHeader(stack, "System Prompt");
            var systemPromptBox = CreateTextArea(entry.ShortDescription, 3, true);
            // System prompt is not stored per-project in ProjectEntry; it's in the main window.
            // We skip it here since it's an app-level setting, not project-specific.

            // ── Short Description ──
            AddSectionHeader(stack, "Short Description");
            AddHint(stack, "A brief summary of the active project, sent with each task.");
            var shortDescBox = CreateTextArea(entry.ShortDescription, 3);
            stack.Children.Add(shortDescBox);
            AddSaveRow(stack, () =>
            {
                entry.ShortDescription = shortDescBox.Text;
                saveProjects();
            });

            AddSeparator(stack);

            // ── Long Description ──
            AddSectionHeader(stack, "Long Description");
            AddHint(stack, "Detailed project description used when Extended Planning is enabled.");
            var longDescBox = CreateTextArea(entry.LongDescription, 5);
            stack.Children.Add(longDescBox);
            AddSaveRow(stack, () =>
            {
                entry.LongDescription = longDescBox.Text;
                saveProjects();
            });

            AddSeparator(stack);

            // ── Rule Instruction ──
            AddSectionHeader(stack, "Rule Instruction");
            AddHint(stack, "Project-specific rules passed with every prompt.");
            var ruleBox = CreateTextArea(entry.RuleInstruction, 3);
            stack.Children.Add(ruleBox);
            AddSaveRow(stack, () =>
            {
                entry.RuleInstruction = ruleBox.Text;
                saveProjects();
            });

            AddSeparator(stack);

            // ── Project Rules List ──
            AddSectionHeader(stack, "Project Rules");
            var rulesPanel = new StackPanel();
            var ruleInputBox = new TextBox
            {
                Background = (Brush)Application.Current.FindResource("BgDeep"),
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                CaretBrush = (Brush)Application.Current.FindResource("TextLight"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(6, 4, 6, 4)
            };

            void RebuildRulesList()
            {
                rulesPanel.Children.Clear();
                foreach (var rule in entry.ProjectRules.ToList())
                {
                    var row = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 2, 0, 2),
                        Background = (Brush)Application.Current.FindResource("BgSection"),
                        BorderThickness = new Thickness(1),
                        BorderBrush = (Brush)Application.Current.FindResource("BgElevated")
                    };
                    var dp = new DockPanel();
                    var removeBtn = new Button
                    {
                        Content = "\uE711",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 9,
                        Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(4, 2, 4, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(removeBtn, Dock.Right);
                    var ruleText = rule;
                    removeBtn.Click += (_, _) =>
                    {
                        entry.ProjectRules.Remove(ruleText);
                        saveProjects();
                        RebuildRulesList();
                    };
                    dp.Children.Add(removeBtn);
                    dp.Children.Add(new TextBlock
                    {
                        Text = rule,
                        Foreground = (Brush)Application.Current.FindResource("TextLight"),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    row.Child = dp;
                    rulesPanel.Children.Add(row);
                }
            }

            RebuildRulesList();
            stack.Children.Add(rulesPanel);

            var addRuleRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var addRuleBtn = new Button
            {
                Content = "Add",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(10, 4, 10, 4)
            };
            DockPanel.SetDock(addRuleBtn, Dock.Right);
            addRuleBtn.Margin = new Thickness(6, 0, 0, 0);
            void AddRule()
            {
                var r = ruleInputBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(r)) return;
                entry.ProjectRules.Add(r);
                saveProjects();
                ruleInputBox.Clear();
                RebuildRulesList();
            }
            addRuleBtn.Click += (_, _) => AddRule();
            ruleInputBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { AddRule(); ke.Handled = true; }
            };
            addRuleRow.Children.Add(addRuleBtn);
            addRuleRow.Children.Add(ruleInputBox);
            stack.Children.Add(addRuleRow);

            AddSeparator(stack);

            // ── Project Type ──
            AddSectionHeader(stack, "Project Type");
            var gameToggle = new ToggleButton
            {
                IsChecked = entry.IsGame,
                Style = (Style)Application.Current.FindResource("ToggleSwitch"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            gameToggle.Content = new TextBlock
            {
                Text = "Game project",
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            };
            gameToggle.Checked += (_, _) =>
            {
                entry.IsGame = true;
                saveProjects();
                onMcpVisibilityChanged?.Invoke();
            };
            gameToggle.Unchecked += (_, _) =>
            {
                entry.IsGame = false;
                saveProjects();
                onMcpVisibilityChanged?.Invoke();
            };
            stack.Children.Add(gameToggle);
            AddHint(stack, "When enabled, game creation rules are automatically included with every task.");

            AddSeparator(stack);

            // ── Log Paths ──
            AddSectionHeader(stack, "Log Paths");
            AddHint(stack, "Used by Build Investigation to locate crash and error logs.");

            AddFieldLabel(stack, "Crash Log");
            var crashLogBox = CreateSingleLineInput(
                !string.IsNullOrEmpty(entry.CrashLogPath) ? entry.CrashLogPath : ProjectManager.GetDefaultCrashLogPath());
            stack.Children.Add(crashLogBox);

            AddFieldLabel(stack, "App Log");
            var appLogBox = CreateSingleLineInput(
                !string.IsNullOrEmpty(entry.AppLogPath) ? entry.AppLogPath : ProjectManager.GetDefaultAppLogPath());
            stack.Children.Add(appLogBox);

            AddFieldLabel(stack, "Hang Log");
            var hangLogBox = CreateSingleLineInput(
                !string.IsNullOrEmpty(entry.HangLogPath) ? entry.HangLogPath : ProjectManager.GetDefaultHangLogPath());
            stack.Children.Add(hangLogBox);

            var logBtnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var resetLogBtn = new Button
            {
                Content = "Reset",
                Style = Application.Current.TryFindResource("GhostBtn") as Style,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            resetLogBtn.Click += (_, _) =>
            {
                crashLogBox.Text = ProjectManager.GetDefaultCrashLogPath();
                appLogBox.Text = ProjectManager.GetDefaultAppLogPath();
                hangLogBox.Text = ProjectManager.GetDefaultHangLogPath();
            };
            var saveLogBtn = new Button
            {
                Content = "Save",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(10, 4, 10, 4)
            };
            saveLogBtn.Click += (_, _) =>
            {
                entry.CrashLogPath = crashLogBox.Text.Trim();
                entry.AppLogPath = appLogBox.Text.Trim();
                entry.HangLogPath = hangLogBox.Text.Trim();
                saveProjects();
            };
            logBtnRow.Children.Add(resetLogBtn);
            logBtnRow.Children.Add(saveLogBtn);
            stack.Children.Add(logBtnRow);

            AddSeparator(stack);

            // ── MCP Settings ──
            AddSectionHeader(stack, "MCP Server");
            AddHint(stack, "Configure the Model Context Protocol server for this project.");

            AddFieldLabel(stack, "Server Name");
            var mcpNameBox = CreateSingleLineInput(entry.McpServerName);
            stack.Children.Add(mcpNameBox);

            AddFieldLabel(stack, "Server Address");
            var mcpAddrBox = CreateSingleLineInput(entry.McpAddress);
            stack.Children.Add(mcpAddrBox);

            AddFieldLabel(stack, "Start Command");
            var mcpCmdBox = CreateSingleLineInput(entry.McpStartCommand);
            stack.Children.Add(mcpCmdBox);

            var saveMcpBtn = new Button
            {
                Content = "Save MCP Settings",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            saveMcpBtn.Click += (_, _) =>
            {
                entry.McpServerName = mcpNameBox.Text?.Trim() ?? "mcp-for-unity-server";
                entry.McpAddress = mcpAddrBox.Text?.Trim() ?? "http://127.0.0.1:8080/mcp";
                entry.McpStartCommand = mcpCmdBox.Text?.Trim() ?? "";
                saveProjects();
            };
            stack.Children.Add(saveMcpBtn);

            // ── Color ──
            AddSeparator(stack);
            AddSectionHeader(stack, "Project Color");
            var colorInput = CreateSingleLineInput(entry.Color);
            colorInput.Width = 120;
            colorInput.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(colorInput);
            var saveColorBtn = new Button
            {
                Content = "Save",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            saveColorBtn.Click += (_, _) =>
            {
                entry.Color = colorInput.Text?.Trim() ?? "";
                saveProjects();
            };
            stack.Children.Add(saveColorBtn);

            scroll.Content = stack;
            dlg.SetBodyContent(scroll);
            dlg.ShowDialog();
        }

        private static void AddSectionHeader(StackPanel parent, string text)
        {
            parent.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        private static void AddFieldLabel(StackPanel parent, string text)
        {
            parent.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 4, 0, 2)
            });
        }

        private static void AddHint(StackPanel parent, string text)
        {
            parent.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("TextDisabled"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        private static void AddSeparator(StackPanel parent)
        {
            parent.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Margin = new Thickness(0, 12, 0, 12)
            });
        }

        private static void AddSaveRow(StackPanel parent, Action onSave)
        {
            var btn = new Button
            {
                Content = "Save",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.Click += (_, _) => onSave();
            parent.Children.Add(btn);
        }

        private static TextBox CreateTextArea(string text, int minLines, bool readOnly = false)
        {
            return new TextBox
            {
                Text = text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinLines = minLines,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = (Brush)Application.Current.FindResource("BgDeep"),
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                CaretBrush = (Brush)Application.Current.FindResource("TextLight"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(8, 6, 8, 6),
                IsReadOnly = readOnly,
                Opacity = readOnly ? 0.6 : 1.0
            };
        }

        private static TextBox CreateSingleLineInput(string text)
        {
            return new TextBox
            {
                Text = text,
                Background = (Brush)Application.Current.FindResource("BgDeep"),
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                CaretBrush = (Brush)Application.Current.FindResource("TextLight"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 2)
            };
        }
    }
}
