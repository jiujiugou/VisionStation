using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Converters;

public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LogMsgLevel level)
        {
            return level switch
            {
                LogMsgLevel.Error => new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
                LogMsgLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                LogMsgLevel.Success => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                _ => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToCameraColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
            : new SolidColorBrush(Colors.LightGray);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class LogLevelToBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LogMsgLevel level)
        {
            return level switch
            {
                LogMsgLevel.Error => new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9)),
                LogMsgLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE0)),
                _ => new SolidColorBrush(Colors.Transparent)
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToOnlineColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x6E, 0xBF, 0xEF))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
