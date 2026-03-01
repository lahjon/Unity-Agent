using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HappyEngine.Dialogs
{
    public partial class DarkDialogWindow : Window
    {
        /// <summary>
        /// The title bar DockPanel, exposed so callers can add extra elements (e.g. subtitle, path text).
        /// </summary>
        public DockPanel TitleBarPanel => TitleBar;

        /// <summary>
        /// The title TextBlock, exposed so callers can customize (e.g. MaxWidth, TextTrimming).
        /// </summary>
        public TextBlock TitleTextBlock => TitleBlock;

        /// <summary>
        /// The outer Border, exposed so callers that need direct access to the root border
        /// (e.g. for legacy compatibility) can use it.
        /// </summary>
        public Border DialogBorder => OuterBorder;

        public DarkDialogWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Creates a configured DarkDialogWindow, mirroring the old DialogFactory.CreateDarkWindow API.
        /// Sets owner, centering, escape-to-close, and optional parameters.
        /// </summary>
        public static DarkDialogWindow Create(
            string title, double width, double height,
            ResizeMode resizeMode = ResizeMode.NoResize,
            bool topmost = true,
            string backgroundResource = "BgSurface",
            string titleColorResource = "Accent")
        {
            var dlg = new DarkDialogWindow
            {
                Title = title,
                Width = width,
                Height = height,
                ResizeMode = resizeMode,
                Topmost = topmost
            };

            // Set title text and color
            dlg.TitleBlock.Text = title;
            try
            {
                dlg.TitleBlock.Foreground = (Brush)Application.Current.FindResource(titleColorResource);
            }
            catch { /* keep default from XAML */ }

            // Set background resource
            try
            {
                dlg.OuterBorder.Background = (Brush)Application.Current.FindResource(backgroundResource);
            }
            catch { /* keep default from XAML */ }

            // Set owner
            Window? owner = null;
            try { owner = Application.Current.MainWindow; }
            catch (Exception ex) { Managers.AppLogger.Debug("DarkDialogWindow", $"MainWindow not available: {ex.Message}"); }

            if (owner != null)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return dlg;
        }

        /// <summary>
        /// Sets the dialog body content (the area below the title bar).
        /// </summary>
        public void SetBodyContent(UIElement content)
        {
            BodyContent.Content = content;
        }

        private void OuterBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void CloseButton_MouseEnter(object sender, MouseEventArgs e)
        {
            CloseButton.Foreground = (Brush)Application.Current.FindResource("TextPrimary");
        }

        private void CloseButton_MouseLeave(object sender, MouseEventArgs e)
        {
            CloseButton.Foreground = (Brush)Application.Current.FindResource("TextSubdued");
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
