using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Support_Tool.ViewModels;

namespace Support_Tool.Views;

public partial class InstallPage : UserControl
{
    private const double MinGridItemWidth = 300;

    public InstallPage()
    {
        InitializeComponent();
    }

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null)
        {
            if (dep is FrameworkElement fe && fe.DataContext is InstallableAppViewModel vm)
            {
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

    // One category = one UniformGrid, so unlike DebloaterPage's single-grid version
    // every category's grid needs the same column count applied.
    private void ApplyGridColumns(double availableWidth)
    {
        var effectiveWidth = Math.Max(0, availableWidth - 24);
        var columns = Math.Max(1, (int)(effectiveWidth / MinGridItemWidth));

        foreach (var panel in FindVisualChildren<UniformGrid>(CategoriesControl))
            panel.Columns = columns;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) yield return target;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
