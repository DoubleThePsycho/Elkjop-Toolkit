using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Support_Tool.Converters;

[ValueConversion(typeof(object), typeof(Visibility))]
public class NullToVisibilityConverter : IValueConverter
{
    public bool CollapseWhenNull { get; set; } = true;

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase);
        bool isEmpty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        bool show = invert ? isEmpty : !isEmpty;
        if (!show)
            return CollapseWhenNull ? Visibility.Collapsed : Visibility.Hidden;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
