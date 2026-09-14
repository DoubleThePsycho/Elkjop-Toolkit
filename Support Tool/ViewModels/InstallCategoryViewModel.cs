using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Support_Tool.ViewModels;

public class InstallCategoryViewModel : ViewModelBase
{
    public InstallCategoryViewModel(string name, IEnumerable<InstallableAppViewModel> items, bool showSelectAll = false)
    {
        Name = name;
        ShowSelectAll = showSelectAll;
        Items = new ObservableCollection<InstallableAppViewModel>(items);

        foreach (var item in Items)
            item.PropertyChanged += OnItemPropertyChanged;

        SelectAllCommand = new RelayCommand(ToggleSelectAll);
    }

    public string Name { get; }
    public bool ShowSelectAll { get; }
    public ObservableCollection<InstallableAppViewModel> Items { get; }
    public RelayCommand SelectAllCommand { get; }

    // Incremental population - appends one item as it streams in from InstallPageViewModel's
    // discovery loop (see its own remarks), rather than requiring the whole category's items to be
    // known up front. Same subscription discipline as the constructor's own loop above.
    public void AddItem(InstallableAppViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
        OnPropertyChanged(nameof(SelectAllState));
    }

    // true = every item selected, false = none selected, null = some selected
    // (drives the header checkbox's indeterminate display state).
    public bool? SelectAllState
    {
        get
        {
            if (Items.Count == 0) return false;
            if (Items.All(i => i.IsSelected)) return true;
            if (Items.All(i => !i.IsSelected)) return false;
            return null;
        }
    }

    private void ToggleSelectAll()
    {
        var selectAll = SelectAllState != true;
        foreach (var item in Items)
            item.IsSelected = selectAll;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallableAppViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectAllState));
    }
}
