using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class SidebarView
{
    private readonly SidebarViewModel _vm;

    public SidebarView(SidebarViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is NavItem item)
        {
            _vm.NavigateTo(item.Label);
        }
    }
}
