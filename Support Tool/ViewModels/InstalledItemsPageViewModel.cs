using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using Support_Tool.Models;
using Support_Tool.Services;
using Support_Tool.Views;

namespace Support_Tool.ViewModels;

public abstract class InstalledItemsPageViewModel : ViewModelBase
{
    private readonly UninstallService _uninstallService;
    private readonly IRestorePointService _restoreService;
    private bool _isLoading;
    private bool _isUninstalling;
    private string _statusMessage = "";
    private int _totalItems;
    private int _completedItems;
    private string _currentItemName = "";

    protected InstalledItemsPageViewModel(
        UninstallService uninstallService,
        IRestorePointService restoreService,
        SharedViewState viewState)
    {
        _uninstallService = uninstallService;
        _restoreService = restoreService;
        ViewState = viewState;

        UninstallCommand   = new AsyncRelayCommand(UninstallAsync, () => SelectedCount > 0);
        SetGridViewCommand = new RelayCommand(() => ViewState.IsGridView = true);
        SetListViewCommand = new RelayCommand(() => ViewState.IsGridView = false);
    }

    public SharedViewState ViewState { get; }
    public ObservableCollection<InstalledItemViewModel> Items { get; } = new();
    public AsyncRelayCommand UninstallCommand { get; }
    public RelayCommand SetGridViewCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public bool HasLoaded { get; private set; }

    public int SelectedCount => Items.Count(x => x.IsSelected && x.IsEnabled);

    protected abstract string PageType { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsUninstalling
    {
        get => _isUninstalling;
        private set => SetProperty(ref _isUninstalling, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // Real batch progress - an honest numerator/denominator (the selected item count is known
    // up front and never changes mid-batch), not an animated/invented percentage. Fed by
    // UninstallService's own UninstallBatchProgress reports (see UninstallAsync below); a
    // failed/skipped/started item still advances CompletedItems exactly like a succeeded one, so
    // the batch always reaches TotalItems once every selected item has been processed one way or
    // another. Shared by both Apps (Debloater) and Programs - same state, same wording.
    public int TotalItems
    {
        get => _totalItems;
        private set
        {
            if (SetProperty(ref _totalItems, value))
            {
                OnPropertyChanged(nameof(UninstallProgressPercent));
                OnPropertyChanged(nameof(UninstallProgressText));
            }
        }
    }

    public int CompletedItems
    {
        get => _completedItems;
        private set
        {
            if (SetProperty(ref _completedItems, value))
            {
                OnPropertyChanged(nameof(UninstallProgressPercent));
                OnPropertyChanged(nameof(UninstallProgressText));
            }
        }
    }

    public string CurrentItemName
    {
        get => _currentItemName;
        private set
        {
            if (SetProperty(ref _currentItemName, value))
            {
                OnPropertyChanged(nameof(UninstallProgressText));
            }
        }
    }

    // 0-100 for a determinate ProgressBar (Minimum=0/Maximum=100) - never a fake/animated value.
    public double UninstallProgressPercent => TotalItems > 0 ? CompletedItems * 100.0 / TotalItems : 0;

    // Compact text for the active-uninstall footer - e.g. "Removing Microsoft OneNote - 3 of 8
    // complete", generic enough to also read sensibly for a classic desktop program.
    public string UninstallProgressText
    {
        get
        {
            if (TotalItems <= 0)
            {
                return "";
            }

            return string.IsNullOrEmpty(CurrentItemName)
                ? $"{CompletedItems} of {TotalItems} complete"
                : $"Removing {CurrentItemName} - {CompletedItems} of {TotalItems} complete";
        }
    }

    public async Task LoadAsync()
    {
        HasLoaded = true;
        IsLoading = true;

        foreach (var old in Items)
            old.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();

        try
        {
            var loaded = await FetchItemsAsync();
            foreach (var item in loaded)
            {
                item.PropertyChanged += OnItemPropertyChanged;
                Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifySelectionChanged();
        }
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledItemViewModel.IsSelected))
            NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        UninstallCommand.RaiseCanExecuteChanged();
    }

    protected abstract Task<IEnumerable<InstalledItemViewModel>> FetchItemsAsync();

    private async Task UninstallAsync()
    {
        var selected = Items
            .Where(x => x.IsSelected && x.IsEnabled)
            .Select(x => x.Item)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "No items are selected for uninstall.",
                "Nothing Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var restoreChoice = MessageBox.Show(
            "Would you like to create a system restore point before uninstalling?",
            "Create Restore Point",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (restoreChoice == MessageBoxResult.Cancel)
            return;

        if (restoreChoice == MessageBoxResult.Yes)
        {
            StatusMessage = "Creating restore point…";
            var (ok, err) = await _restoreService.CreateAsync("Before Elkjøp Support Tool cleanup");
            StatusMessage = "";

            if (!ok)
            {
                var cont = MessageBox.Show(
                    $"Restore point creation failed:\n{err}\n\nContinue with uninstall anyway?",
                    "Restore Point Failed",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (cont != MessageBoxResult.Yes)
                    return;
            }
        }

        if (ViewState.IsLiveMode)
        {
            var dialog = new ConfirmUninstallDialog(selected.Select(i => i.Name).ToList());
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();
            if (!dialog.Confirmed)
                return;
        }
        else
        {
            var confirm = MessageBox.Show(
                $"[DRY RUN]\n\nYou are about to simulate uninstalling {selected.Count} item(s).\n\n" +
                "No software will be removed.\n\nContinue?",
                "Confirm Dry Run",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        IsUninstalling = true;
        TotalItems = selected.Count;
        CompletedItems = 0;
        CurrentItemName = "";
        var progress = new Progress<string>(msg => StatusMessage = msg);
        var batchProgress = new Progress<UninstallBatchProgress>(p =>
        {
            CompletedItems = p.Completed;
            TotalItems = p.Total;
            CurrentItemName = p.CurrentItemName;
        });

        List<UninstallResult> results;
        try
        {
            results = await _uninstallService.UninstallAsync(
                selected, ViewState.ExecutionMode, PageType, progress, batchProgress);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Uninstall operation failed unexpectedly:\n{ex.Message}",
                "Uninstall Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = "";
            return;
        }
        finally
        {
            IsUninstalling = false;
        }

        StatusMessage = "";
        ShowResultSummary(results);
        await LoadAsync();
    }

    private void ShowResultSummary(List<UninstallResult> results)
    {
        bool isDryRun = results.Any(r => r.Status == UninstallStatus.DryRun);

        if (isDryRun)
        {
            MessageBox.Show(
                $"Dry-run complete.\n\n{results.Count} item(s) would have been processed.\n" +
                "No real software was removed.",
                "Dry-Run Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var succeeded = results.Where(r => r.Status == UninstallStatus.Succeeded).ToList();
        var started   = results.Where(r => r.Status == UninstallStatus.Started).ToList();
        var failed    = results.Where(r => r.Status == UninstallStatus.Failed).ToList();
        var skipped   = results.Where(r => r.Status == UninstallStatus.Skipped).ToList();
        var cancelled = results.Where(r => r.Status == UninstallStatus.Cancelled).ToList();

        var sb = new StringBuilder();
        if (succeeded.Count > 0) sb.AppendLine($"Removed:             {succeeded.Count}");
        if (started.Count   > 0) sb.AppendLine($"Uninstaller started: {started.Count}");
        if (failed.Count    > 0) sb.AppendLine($"Failed:              {failed.Count}");
        if (skipped.Count   > 0) sb.AppendLine($"Skipped:             {skipped.Count}");
        if (cancelled.Count > 0) sb.AppendLine($"Cancelled:           {cancelled.Count}");

        if (started.Count > 0)
            sb.AppendLine("\nItems marked 'Uninstaller started' may need verification after a reboot.");

        var issues = failed.Concat(skipped).ToList();
        if (issues.Count > 0)
        {
            sb.AppendLine("\nDetails:");
            foreach (var r in issues)
                sb.AppendLine($"  • {r.Item.Name}: {r.ErrorMessage ?? "Unknown error"}");
        }

        var icon = failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        MessageBox.Show(sb.ToString().Trim(), "Uninstall Complete", MessageBoxButton.OK, icon);
    }
}
