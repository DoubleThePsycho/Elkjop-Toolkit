using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Support_Tool.ViewModels;

namespace Support_Tool.Views;

public partial class ProgramsPage : UserControl
{
    public ProgramsPage()
    {
        InitializeComponent();
    }

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null)
        {
            if (dep is FrameworkElement fe && fe.DataContext is InstalledItemViewModel vm)
            {
                if (vm.IsEnabled)
                    vm.IsSelected = !vm.IsSelected;
                e.Handled = true;
                return;
            }
            dep = VisualTreeHelper.GetParent(dep);
        }
    }

    private void ContentArea_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyGridColumns(e.NewSize.Width);

    private void GridScrollViewer_Loaded(object sender, RoutedEventArgs e)
        => ApplyGridColumns(ContentArea.ActualWidth);

    private void ApplyGridColumns(double availableWidth)
    {
        var panel = GridColumnHelper.FindVisualChild<WrapPanel>(GridItemsControl);
        if (panel == null) return;

        var columns = GridColumnHelper.CalculateColumns(availableWidth);
        panel.ItemWidth = GridColumnHelper.CalculateItemWidth(availableWidth, columns);
    }
}
