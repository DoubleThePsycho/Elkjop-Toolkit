using System.ComponentModel;
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

    // Explicit subscription lifetime, tracked directly rather than inferred from
    // DataContextChanged - InstallPageViewModel is a long-lived process singleton, but a fresh
    // InstallPage instance is created by the DataTemplate every time the technician navigates
    // back to Install (MainWindow's ContentControl re-templates on each CurrentPage change).
    // DataContextChanged is not a reliable unsubscribe hook: a DataTemplate-generated root
    // element's DataContext can simply stop being read once the element is detached from the
    // visual tree, with no guarantee DataContextChanged ever fires again to report the "old"
    // value - relying on it would leave the singleton ViewModel holding a PropertyChanged
    // subscription (and therefore a strong reference) to an arbitrary number of stale, otherwise
    // unloaded InstallPage instances. Loaded/Unloaded are the actual visual-tree lifetime hooks;
    // _subscribedViewModel makes "never double-subscribe" and "always unsubscribe from the right
    // instance" explicit rather than assumed.
    private InstallPageViewModel? _subscribedViewModel;
    private bool _isLoaded;

    public InstallPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        SubscribeToViewModel(DataContext as InstallPageViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        SubscribeToViewModel(null);
    }

    // Only acts while actually loaded - if DataContext changes before Loaded ever fires (or after
    // Unloaded), OnLoaded/OnUnloaded themselves already own subscribing/clearing at the right
    // moments, so this only needs to handle a DataContext swap on an already-loaded instance.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_isLoaded)
        {
            SubscribeToViewModel(e.NewValue as InstallPageViewModel);
        }
    }

    private void SubscribeToViewModel(InstallPageViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Keeps the "Preparing WinGet" detail log scrolled to its latest line as new streamed output
    // arrives, since a plain data-bound TextBlock inside a ScrollViewer doesn't do that on its
    // own.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallPageViewModel.PrerequisiteDetailText))
        {
            PrerequisiteDetailScrollViewer.ScrollToEnd();
        }
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
