using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Support_Tool.ViewModels;

namespace Support_Tool.Views;

public partial class DebloaterPage : UserControl
{
    private const double MinGridItemWidth = 300;

    public DebloaterPage()
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
        var panel = FindVisualChild<UniformGrid>(GridItemsControl);
        if (panel == null) return;
        // Subtract ScrollViewer padding (12px each side) and item margins (3px each side)
        var effectiveWidth = Math.Max(0, availableWidth - 24);
        panel.Columns = Math.Max(1, (int)(effectiveWidth / MinGridItemWidth));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) return target;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
