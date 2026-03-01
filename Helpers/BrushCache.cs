using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace HappyEngine.Helpers
{
    public static class BrushCache
    {
        private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new();

        public static SolidColorBrush Get(string hex)
        {
            return Cache.GetOrAdd(hex, static h =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
                brush.Freeze();
                return brush;
            });
        }

        /// <summary>
        /// Looks up a named SolidColorBrush from the application's theme resources (Themes/Colors.xaml).
        /// Falls back to TextMuted (#666666) if the resource is not found or the application is not available.
        /// </summary>
        public static SolidColorBrush Theme(string key)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
                return brush;
            return Get("#666666");
        }
    }
}
