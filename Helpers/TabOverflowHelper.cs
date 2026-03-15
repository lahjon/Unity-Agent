using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Spritely
{
    public static class TabOverflowHelper
    {
        public static void SetupOverflow(TabControl tabControl)
        {
            tabControl.ApplyTemplate();
            var btn = tabControl.Template.FindName("PART_OverflowButton", tabControl) as Button;
            if (btn != null)
                btn.Click += (s, e) => ShowOverflowPopup(s as Button, tabControl);
        }

        private static void ShowOverflowPopup(Button? btn, TabControl tabControl)
        {
            if (btn == null) return;

            var popup = new Popup
            {
                PlacementTarget = btn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgPopup"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 130,
                MaxHeight = 350
            };

            var stack = new StackPanel();

            foreach (var item in tabControl.Items)
            {
                if (item is TabItem tab && tab.Visibility == Visibility.Visible)
                {
                    string text = GetTabHeaderText(tab);
                    bool isSelected = tab == tabControl.SelectedItem;

                    var itemBorder = new Border
                    {
                        Background = isSelected
                            ? (Brush)Application.Current.FindResource("BgHover")
                            : Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 1, 0, 1),
                        Cursor = Cursors.Hand
                    };

                    var textBlock = new TextBlock
                    {
                        Text = text,
                        Foreground = isSelected
                            ? (Brush)Application.Current.FindResource("Accent")
                            : (Brush)Application.Current.FindResource("TextBody"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal
                    };

                    itemBorder.Child = textBlock;

                    var capturedTab = tab;
                    itemBorder.MouseEnter += (_, _) =>
                    {
                        if (capturedTab != tabControl.SelectedItem)
                            itemBorder.Background = (Brush)Application.Current.FindResource("BgElevated");
                    };
                    itemBorder.MouseLeave += (_, _) =>
                    {
                        if (capturedTab != tabControl.SelectedItem)
                            itemBorder.Background = Brushes.Transparent;
                    };
                    itemBorder.MouseLeftButtonDown += (_, _) =>
                    {
                        tabControl.SelectedItem = capturedTab;
                        popup.IsOpen = false;
                    };

                    stack.Children.Add(itemBorder);
                }
            }

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        private static string GetTabHeaderText(TabItem tab)
        {
            if (tab.Header is TextBlock tb) return tb.Text;
            if (tab.Header is StackPanel sp)
            {
                foreach (var child in sp.Children)
                    if (child is TextBlock t) return t.Text;
            }
            if (tab.Header is string s) return s;
            return "Tab";
        }
    }
}
