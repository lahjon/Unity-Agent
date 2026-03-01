using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HappyEngine.Helpers;

namespace HappyEngine.Converters
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
            return BrushCache.Theme("TextDisabled");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
