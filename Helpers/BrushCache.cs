using System.Collections.Concurrent;
using System.Windows.Media;

namespace AgenticEngine.Helpers
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
    }
}
