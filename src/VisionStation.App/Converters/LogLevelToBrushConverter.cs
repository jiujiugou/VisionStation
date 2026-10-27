using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Converters;

// 这个文件里全是"值 → 颜色/可见性"的纯函数转换器，只给 XAML 用。
//
// 三个共同约定：
//   1. 只做 Convert，ConvertBack 一律抛 NotImplementedException ——
//      这些都是单向显示类绑定，反向写回没有语义。抛异常比返回 null 好排查：
//      谁要是接了 TwoWay，绑定会立刻在输出窗口报错，而不是悄悄把值改成默认值。
//   2. 每次转换 new 一个 SolidColorBrush 而不复用静态实例：WPF 的 Brush 如果在
//      多个线程/多个元素间共享，且被 Freeze 过，后续改色会抛 InvalidOperationException。
//      这些转换器调用频率极低（日志追加、状态翻面），不值得为它引入共享状态。
//   3. 颜色值是产品视觉规范的一部分，改动会影响操作员对"红=故障"的条件反射，
//      不要在文件里换成别的近似色。

/// <summary>
/// 日志级别 → 文字颜色。绑定在日志面板的 Foreground 上。
/// 红/橙/绿三色与现场习惯一致：红=故障、橙=警告、绿=恢复或成功。
/// </summary>
public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LogMsgLevel level)
        {
            return level switch
            {
                // #E81123：Windows 系统错误红，投影仪/工业屏上都不发灰
                LogMsgLevel.Error => new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
                // #FF8C00：深橙，比纯黄在浅色背景上可读
                LogMsgLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                // #107C10：Windows 成功绿，与错误红明度接近，一眼能分辨
                LogMsgLevel.Success => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                // 普通信息压成深灰 #555，弱化到"需要时才看"的层级
                _ => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
            };
        }

        // 值不是 LogMsgLevel（绑定还没求值、或 DataContext 为空）时给中性灰，
        // 不要落成黑色 —— 黑色会和"错误"抢视觉重量。
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool → Visibility。true 显示、false 隐藏。
///
/// 用 Collapsed 而不是 Hidden：Hidden 仍占布局位置，侧边栏按钮按角色隐藏后
/// 会留一个空洞，看起来像界面没加载完。
/// </summary>
public class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool → 状态圆点颜色（绿/浅灰）。用于控制面板里的设备状态指示。
///
/// <para>
/// 名字里的 Camera 是历史遗留：2026-10-26 相机层整体删除后，相机不再由本程序管理，
/// 这个转换器现在被"平台/设备是否在线"的指示灯复用。
/// <b>改名会破坏 XAML 里的 StaticResource 键</b>，所以保留旧名，改的时候要同时改资源字典。
/// </para>
/// </summary>
public class BoolToCameraColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))   // 在线：同"成功绿"
            : new SolidColorBrush(Colors.LightGray);                  // 离线：浅灰，不用红

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 日志级别 → 整行背景色。错误浅红、警告浅橙、其余透明。
///
/// 与 <see cref="LogLevelToBrushConverter"/> 的分工：那个管文字颜色（所有级别都看得清），
/// 这个只管"让错误行在滚动列表里跳出来"。背景用很浅的色号是关键 ——
/// 深色背景会把文字压成一片，反而更难看。
/// </summary>
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
                // Info / Success 不加底色：日志里绝大多数是这两类，全上色等于全不上色
                _ => new SolidColorBrush(Colors.Transparent)
            };
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool → 顶栏"在线"文字颜色（浅蓝/灰）。
///
/// 和 <see cref="BoolToCameraColorConverter"/> 的区别是色相：那个给圆形指示灯，
/// 这个给文字，浅蓝 (#6EBFEF) 在深色顶栏上比绿色更稳，不会和"OK 判定绿"混淆。
/// </summary>
public class BoolToOnlineColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x6E, 0xBF, 0xEF))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
