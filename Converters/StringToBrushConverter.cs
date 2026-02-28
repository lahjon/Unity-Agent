using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgenticEngine.Helpers;

namespace AgenticEngine.Converters
{
    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string colorStr && !string.IsNullOrEmpty(colorStr))
            {
                try
                {
                    return BrushCache.Get(colorStr);
                }
                catch (Exception ex) { Managers.AppLogger.Debug("StringToBrushConverter", $"Invalid color string '{colorStr}'", ex); }
            }
            try { return (Brush)Application.Current.FindResource("TextDisabled"); }
            catch (Exception ex) { Managers.AppLogger.Debug("StringToBrushConverter", $"TextDisabled resource not found: {ex.Message}"); return BrushCache.Get("#555555"); }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
