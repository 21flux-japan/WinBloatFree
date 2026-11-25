using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinBloatFree;

/// <summary>True becomes Collapsed, false becomes Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && v != Visibility.Visible;
}

/// <summary>Non-empty strings and non-empty lists are Visible.</summary>
public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        string s => s.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        System.Collections.ICollection c => c.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        null => Visibility.Collapsed,
        _ => Visibility.Visible,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
