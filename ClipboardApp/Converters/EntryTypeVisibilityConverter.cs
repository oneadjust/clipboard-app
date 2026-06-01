using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ClipboardApp.Models;

namespace ClipboardApp.Converters;

public sealed class EntryTypeVisibilityConverter : IValueConverter
{
    public ClipboardEntryType Match { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ClipboardEntryType type && type == Match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
