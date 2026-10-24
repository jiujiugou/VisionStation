using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

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
