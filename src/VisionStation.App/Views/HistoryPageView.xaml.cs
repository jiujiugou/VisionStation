using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 历史记录页视图。code-behind 只有一件事：**首次显示时触发一次查询**。
///
/// <para>
/// 为什么这件事不能放 VM 构造函数：VM 是单例，在导航服务构造时就被解析，
/// 那时查库会拖慢冷启动，而且用户可能整班都不打开这一页。
/// </para>
/// <para>
/// 为什么判 <c>Rows.Count == 0</c>：已有数据就不再查，否则每次切页都重查，
/// 刚才设的筛选条件与结果会反复闪。
/// </para>
/// </summary>
public partial class HistoryPageView : UserControl
{
    public HistoryPageView()
    {
        InitializeComponent();

        // 首次显示时查一次。
        //
        // 放 Loaded 而不是构造函数：ViewModel 是单例，构造发生在启动组装阶段，
        // 那时查库会拖慢冷启动；而且用户可能整班都不打开这一页。
        // 表里已经有数据就不再查 —— 每次切页重查会让刚才的筛选条件与结果反复闪。
        Loaded += (_, _) =>
        {
            if (DataContext is HistoryPageViewModel { Rows.Count: 0 } viewModel)
                viewModel.RefreshCommand.Execute(null);
        };
    }
}
