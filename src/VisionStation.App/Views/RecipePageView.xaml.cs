using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 配方切换页视图。code-behind 只有一件事：**首次显示时读一次配方目录**。
///
/// <para>
/// 为什么不能放 VM 构造函数：配方目录是磁盘 IO，VM 是单例、在组合根组装期就被解析，
/// 那时读盘会拖慢冷启动，而且用户可能整班都不打开这一页（AGENTS.md 第 7 条）。
/// </para>
/// <para>
/// 为什么判 <c>Rows.Count == 0</c>：已有列表就保留上一次的结果，
/// 否则每次切页都重读，选中项会反复闪回激活项。
/// 目录确实为空时每次进页都会再读一次 —— 这正是想要的：有人放了新配方进目录后，
/// 切回来就能看到，不用先去点「刷新」。
/// </para>
/// </summary>
public partial class RecipePageView : UserControl
{
    public RecipePageView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is RecipePageViewModel { Rows.Count: 0 } viewModel)
                viewModel.RefreshCommand.Execute(null);
        };
    }
}
