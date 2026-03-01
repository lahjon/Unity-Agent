using System;
using System.Globalization;
using System.Windows.Data;

namespace HappyEngine.Converters
{
    public class WrapTooltipTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string text)
                return value;

            int maxLineLength = 80;
            if (parameter is string paramStr && int.TryParse(paramStr, out var parsed))
                maxLineLength = parsed;

            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength)
                return text;

            var sb = new System.Text.StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length > 0) sb.Append('\n');
                var remaining = line;
                while (remaining.Length > maxLineLength)
                {
                    int breakAt = remaining.LastIndexOf(' ', maxLineLength);
                    if (breakAt <= 0) breakAt = maxLineLength;
                    sb.Append(remaining, 0, breakAt);
                    sb.Append('\n');
                    remaining = remaining.Substring(breakAt).TrimStart();
                }
                sb.Append(remaining);
            }

            return sb.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
