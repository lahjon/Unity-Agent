using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace HappyEngine.Models
{
    /// <summary>
    /// Abstracts the UI controls that ProjectManager needs, decoupling it from
    /// specific XAML element references so it can be tested with a mock view.
    /// </summary>
    public interface IProjectPanelView
    {
        TextBlock PromptProjectLabel { get; }
        TextBlock AddProjectPath { get; }
        StackPanel ProjectListPanel { get; }
        ToggleButton UseMcpToggle { get; }
        TextBox ShortDescBox { get; }
        TextBox LongDescBox { get; }
        TextBox RuleInstructionBox { get; }
        ToggleButton EditShortDescToggle { get; }
        ToggleButton EditLongDescToggle { get; }
        ToggleButton EditRuleInstructionToggle { get; }
        ItemsControl ProjectRulesList { get; }
        TextBox CrashLogPathBox { get; }
        TextBox AppLogPathBox { get; }
        TextBox HangLogPathBox { get; }
        ToggleButton EditCrashLogPathsToggle { get; }
        Button RegenerateDescBtn { get; }
        Dispatcher ViewDispatcher { get; }
    }
}
