using System.Globalization;
using System.Windows.Data;

namespace VisionStation.App.Converters;

/// <summary>
/// 恒等转换器：原样返回传入的值。
///
/// <para>
/// ImagePanel 不做额外转换，CurrentImage 已在 VM 中计算好。
/// 此 Converter 保留作为占位，实际绑定直接用 CurrentImage。
/// </para>
/// <para>
/// 保留它的唯一理由：XAML 里已有 <c>Converter={StaticResource CurrentImage}</c> 的绑定，
/// 删掉会连带要改资源字典与所有引用点。它不产生任何运行时开销（一次空返回）。
/// 如果哪天清理 XAML，可以连同资源键一起删。
/// </para>
/// </summary>
public class CurrentImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
