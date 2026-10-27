using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 审计页视图。code-behind 只有一件事：**首次显示时触发一次查询**。
///
/// <para>
/// 理由与历史页完全相同（VM 是单例、构造阶段不该查库、整班没人看这页），
/// 两页刻意用同一套写法：改一处时另一处不会因为"写法不同"而被漏掉。
/// </para>
/// </summary>
public partial class AuditPageView : UserControl
{
    public AuditPageView()
    {
        InitializeComponent();

        // 首次显示时查一次。
        //
        // 放 Loaded 而不是构造函数：ViewModel 是单例，构造发生在启动组装阶段，
        // 那时查库会拖慢冷启动；而且用户可能整班都不打开这一页。
        // 表里已经有数据就不再查 —— 每次切页重查会让刚才的过滤条件与结果反复闪。
        Loaded += (_, _) =>
        {
            if (DataContext is AuditPageViewModel { Rows.Count: 0 } viewModel)
                viewModel.RefreshCommand.Execute(null);
        };
    }
}
