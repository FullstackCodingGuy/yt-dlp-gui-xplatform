using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace YtDlpGui.AvaloniaApp.Converters
{
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !string.IsNullOrWhiteSpace(value as string);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isError && isError)
            {
                return "#EF4444"; // Red for errors
            }
            return "#FFFFFF"; // White for normal
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
