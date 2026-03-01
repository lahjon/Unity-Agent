using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HappyEngine.Converters
{
    public class NestingDepthToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int depth && depth > 0)
                return new Thickness(depth * 20, 2, 0, 2);
            return new Thickness(0, 2, 0, 2);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
