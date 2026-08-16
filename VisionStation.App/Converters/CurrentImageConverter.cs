using System.Globalization;
using System.Windows.Data;

namespace VisionStation.App.Converters;

/// <summary>
/// ImagePanel 不做额外转换，CurrentImage 已在 VM 中计算好。
/// 此 Converter 保留作为占位，实际绑定直接用 CurrentImage。
/// </summary>
public class CurrentImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
