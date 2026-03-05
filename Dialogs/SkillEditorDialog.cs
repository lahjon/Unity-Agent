using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely.Dialogs
{
    /// <summary>
    /// Dialog for managing skills — create, edit, delete, and browse skills.
    /// Built programmatically following the DarkDialog pattern.
    /// </summary>
    public static class SkillEditorDialog
    {
        /// <summary>
        /// Shows the full skill manager dialog. Returns true if any changes were made.
        /// </summary>
        public static bool Show(SkillManager skillManager, string? projectPath)
        {
            var dlg = DarkDialogWindow.Create("Manage Skills", 700, 550);
            bool changed = false;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // toolbar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
            root.Margin = new Thickness(16, 8, 16, 16);

            // ── Toolbar ──────────────────────────────────────────
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(toolbar, 0);

            var addGlobalBtn = new Button
            {
                Content = "＋ Global Skill",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Background = (Brush)Application.Current.FindResource("Accent"),
                Style = Application.Current.TryFindResource("Btn") as Style
            };

            var addProjectBtn = new Button
            {
                Content = "＋ Project Skill",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style,
                IsEnabled = !string.IsNullOrEmpty(projectPath)
            };

            toolbar.Children.Add(addGlobalBtn);
            toolbar.Children.Add(addProjectBtn);
            root.Children.Add(toolbar);

            // ── Skills List ──────────────────────────────────────
            var listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(listScroll, 1);

            var listPanel = new StackPanel();
            listScroll.Content = listPanel;
            root.Children.Add(listScroll);

            // Render helper
            void RenderList()
            {
                listPanel.Children.Clear();

                var allSkills = skillManager.AllSkills;
                if (allSkills.Count == 0)
                {
                    listPanel.Children.Add(new TextBlock
                    {
                        Text = "No skills yet. Click ＋ to create one.",
                        Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                        FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                        Margin = new Thickness(0, 20, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    return;
                }

                // Group by scope
                var groups = new[] {
                    ("🌐 Global Skills", allSkills.Where(s => s.IsGlobal).ToList()),
                    ("📁 Project Skills", allSkills.Where(s => !s.IsGlobal).ToList())
                };

                foreach (var (header, skills) in groups)
                {
                    if (skills.Count == 0) continue;

                    listPanel.Children.Add(new TextBlock
                    {
                        Text = header,
                        Foreground = (Brush)Application.Current.FindResource("Accent"),
                        FontSize = 12, FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                        Margin = new Thickness(0, 8, 0, 4)
                    });

                    foreach (var skill in skills)
                    {
                        var card = BuildSkillCard(skill, skillManager, projectPath, dlg, () =>
                        {
                            changed = true;
                            RenderList();
                        });
                        listPanel.Children.Add(card);
                    }
                }
            }

            addGlobalBtn.Click += (_, _) =>
            {
                if (ShowSkillEditDialog(null, isGlobal: true, skillManager))
                {
                    changed = true;
                    RenderList();
                }
            };

            addProjectBtn.Click += (_, _) =>
            {
                if (ShowSkillEditDialog(null, isGlobal: false, skillManager, projectPath))
                {
                    changed = true;
                    RenderList();
                }
            };

            RenderList();
            dlg.SetBodyContent(root);
            dlg.ShowDialog();
            return changed;
        }

        private static Border BuildSkillCard(SkillEntry skill, SkillManager skillManager,
            string? projectPath, Window parentDlg, Action onChanged)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            card.Style = BuildCardStyle();

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: name + description
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var nameBlock = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary")
            };
            nameBlock.Inlines.Add(new System.Windows.Documents.Run(skill.Name));
            if (!string.IsNullOrWhiteSpace(skill.Category))
            {
                nameBlock.Inlines.Add(new System.Windows.Documents.Run($"  [{skill.Category}]")
                {
                    Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                    FontSize = 10, FontWeight = FontWeights.Normal
                });
            }
            info.Children.Add(nameBlock);

            if (!string.IsNullOrWhiteSpace(skill.Description))
            {
                info.Children.Add(new TextBlock
                {
                    Text = skill.Description,
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 450
                });
            }

            var contentPreview = skill.Content.Length > 80
                ? skill.Content[..80].Replace("\n", " ") + "..."
                : skill.Content.Replace("\n", " ");
            info.Children.Add(new TextBlock
            {
                Text = contentPreview,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10, FontFamily = new FontFamily("Consolas"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 450, Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            // Right: edit + delete buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var scopeLabel = new TextBlock
            {
                Text = skill.IsGlobal ? "🌐" : "📁",
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = skill.IsGlobal ? "Global skill" : "Project skill"
            };
            btnPanel.Children.Add(scopeLabel);

            var editBtn = new Button
            {
                Content = "Edit",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style,
                FontSize = 11
            };
            editBtn.Click += (_, _) =>
            {
                if (ShowSkillEditDialog(skill, skill.IsGlobal, skillManager, projectPath))
                    onChanged();
            };
            btnPanel.Children.Add(editBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                Padding = new Thickness(10, 4, 10, 4),
                Style = Application.Current.TryFindResource("DangerBtn") as Style,
                FontSize = 11
            };
            deleteBtn.Click += (_, _) =>
            {
                if (DarkDialog.ShowConfirm($"Delete skill \"{skill.Name}\"?", "Delete Skill"))
                {
                    skillManager.DeleteSkill(skill, projectPath);
                    onChanged();
                }
            };
            btnPanel.Children.Add(deleteBtn);

            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            card.Child = grid;

            // Double-click to edit
            card.MouseLeftButtonDown += (_, _) =>
            {
                if (ShowSkillEditDialog(skill, skill.IsGlobal, skillManager, projectPath))
                    onChanged();
            };

            return card;
        }

        /// <summary>
        /// Shows a dialog to create or edit a single skill. Returns true if saved.
        /// </summary>
        public static bool ShowSkillEditDialog(SkillEntry? existing, bool isGlobal,
            SkillManager skillManager, string? projectPath = null)
        {
            var isNew = existing == null;
            var title = isNew ? "Create Skill" : "Edit Skill";
            var dlg = DarkDialogWindow.Create(title, 600, 520);

            bool saved = false;
            var stack = new StackPanel { Margin = new Thickness(20, 12, 20, 20) };

            // Name
            stack.Children.Add(MakeLabel("Name"));
            var nameBox = MakeTextBox(existing?.Name ?? "");
            stack.Children.Add(nameBox);

            // Category
            stack.Children.Add(MakeLabel("Category (optional)"));
            var categoryBox = MakeTextBox(existing?.Category ?? "");
            stack.Children.Add(categoryBox);

            // Description
            stack.Children.Add(MakeLabel("Description (optional)"));
            var descBox = MakeTextBox(existing?.Description ?? "");
            stack.Children.Add(descBox);

            // Content
            stack.Children.Add(MakeLabel("Skill Content (Markdown — this gets injected into the prompt)"));
            var contentBox = new TextBox
            {
                Text = existing?.Content ?? "",
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 180,
                MaxHeight = 300,
                Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
                SelectionBrush = (Brush)Application.Current.FindResource("Accent"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(contentBox);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style
            };
            cancelBtn.Click += (_, _) => dlg.Close();

            var saveBtn = new Button
            {
                Content = isNew ? "Create" : "Save",
                Padding = new Thickness(18, 8, 18, 8),
                Background = (Brush)Application.Current.FindResource("Accent"),
                Style = Application.Current.TryFindResource("Btn") as Style
            };
            saveBtn.Click += (_, _) =>
            {
                var name = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    DarkDialog.ShowAlert("Skill name is required.", "Validation");
                    return;
                }

                var content = contentBox.Text?.Trim();
                if (string.IsNullOrEmpty(content))
                {
                    DarkDialog.ShowAlert("Skill content is required.", "Validation");
                    return;
                }

                var skill = existing ?? new SkillEntry();
                skill.Name = name;
                skill.Description = descBox.Text?.Trim() ?? "";
                skill.Category = categoryBox.Text?.Trim() ?? "";
                skill.Content = content;
                skill.IsGlobal = isGlobal;

                if (isNew)
                    skill.CreatedAt = DateTime.Now;

                skillManager.SaveSkill(skill, projectPath);
                saved = true;
                dlg.Close();
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            stack.Children.Add(btnPanel);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack
            };

            dlg.SetBodyContent(scroll);
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                    saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            dlg.ContentRendered += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
            dlg.ShowDialog();
            return saved;
        }

        // ── UI Helpers ──────────────────────────────────────────

        private static TextBlock MakeLabel(string text) => new()
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("TextLight"),
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 0, 0, 4)
        };

        private static TextBox MakeTextBox(string value) => new()
        {
            Text = value,
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)Application.Current.FindResource("BgElevated"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
            CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
            SelectionBrush = (Brush)Application.Current.FindResource("Accent"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        private static Style BuildCardStyle()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty,
                Application.Current.FindResource("BgPopup")));

            var hoverTrigger = new Trigger
            {
                Property = Border.IsMouseOverProperty,
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                Application.Current.FindResource("BgCardHover")));
            style.Triggers.Add(hoverTrigger);

            return style;
        }
    }
}

