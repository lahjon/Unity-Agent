using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

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
                    var color = (Color)ColorConverter.ConvertFromString(colorStr);
                    return new SolidColorBrush(color);
                }
                catch (Exception ex) { Managers.AppLogger.Debug("StringToBrushConverter", $"Invalid color string '{colorStr}': {ex.Message}"); }
            }
            return new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
