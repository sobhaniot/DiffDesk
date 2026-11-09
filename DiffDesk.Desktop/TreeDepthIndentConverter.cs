using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiffDesk.Desktop;

public sealed class TreeDepthIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = value is int number ? Math.Clamp(number, 0, 32) : 0;
        return new Thickness(depth * 18, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
