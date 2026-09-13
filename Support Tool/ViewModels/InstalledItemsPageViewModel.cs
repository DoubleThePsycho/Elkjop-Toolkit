using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Support_Tool.Models;
using Support_Tool.Services;
using Support_Tool.Views;

namespace Support_Tool.ViewModels;

public abstract class InstalledItemsPageViewModel : ViewModelBase
{
    private readonly UninstallService _uninstallService;
    private bool _isLoading;
    private bool _isUninstalling;
    private string _statusMessage = "";
    private string _searchText = "";
    private int _totalItems;
    private int _completedItems;
    private string _currentItemName = "";

    // Bumped on every LoadAsync call - the single source of truth for "is this the current
    // discovery." A background callback (registry read, PowerShell output line) that resolves
    // after a newer LoadAsync has already started compares its own captured generation against
    // this and simply discards itself if they no longer match, rather than mutating Items on
    // behalf of a discovery nothing is showing anymore.
    private int _discoveryGeneration;
    private CancellationTokenSource? _discoveryCts;

    // Defensive dedupe across one discovery generation - both inventory services already
    // guarantee no duplicates on their own (PackageFullName is inherently unique per Appx query;
    // ProgramInventoryService dedupes by name across its three hives internally), this is a
    // second, cheap safety net at the point items actually enter the visible collection.
    private readonly HashSet<string> _seenKeys = new(StringComparer.OrdinalIgnoreCase);

    protected InstalledItemsPageViewModel(
        UninstallService uninstallService,
        SharedViewState viewState)
    {
        _uninstallService = uninstallService;
        ViewState = viewState;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        UninstallCommand   = new AsyncRelayCommand(UninstallAsync, () => SelectedCount > 0);
        SetGridViewCommand = new RelayCommand(() => ViewState.IsGridView = true);
        SetListViewCommand = new RelayCommand(() => ViewState.IsGridView = false);
    }

    public SharedViewState ViewState { get; }
    public ObservableCollection<InstalledItemViewModel> Items { get; } = new();

    // What the two pages' ItemsControl/ListBox actually bind to - a live filtered view over
    // Items, not a second collection. Only ever has a Filter set, deliberately never
    // SortDescriptions: sorting is a one-time Items.Move pass at the end of discovery (see
    // LoadAsync), not something this view re-evaluates on every append.
    public ICollectionView ItemsView { get; }

    public AsyncRelayCommand UninstallCommand { get; }
    public RelayCommand SetGridViewCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public bool HasLoaded { get; private set; }

    public int SelectedCount => Items.Count(x => x.IsSelected && x.IsEnabled);

    protected abstract string PageType { get; }

    // "apps" / "programs" - only used to word the discovery status line naturally for each page.
    protected abstract string ItemsNoun { get; }

    // The one thing each concrete page provides: a live stream of already rule-wrapped rows, in
    // whatever order the underlying inventory service discovers them. Rule lookup/preselection
    // happens inside this method (each subclass loads its own RuleSet once, then wraps every item
    // as it arrives) - by the time a VM reaches LoadAsync's Items.Add, it's already fully formed.
    protected abstract IAsyncEnumerable<InstalledItemViewModel> DiscoverAsync(CancellationToken ct);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    private bool FilterItem(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return obj is InstalledItemViewModel vm
            && (vm.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || vm.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    // True for the whole time a discovery is in flight - drives the small non-blocking status
    // line, never a full-page overlay that would hide already-discovered rows (see
    // DiscoveryStatusText and the pages' own XAML).
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(DiscoveryStatusText));
            }
        }
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

    // "Discovering installed apps... 17 found" while active; empty once discovery finishes (the
    // populated/filtered list itself is the "done" signal - no separate "scan complete" line).
    public string DiscoveryStatusText => IsLoading
        ? $"Discovering installed {ItemsNoun}… {Items.Count} found"
        : "";

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

        // Supersede whatever discovery might still be running - its callbacks compare their own
        // captured generation below and quietly stop mutating state once they no longer match,
        // and the process/registry walk itself is asked to actually stop rather than just being
        // ignored (see the inventory services' own cancellation handling).
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        var cts = new CancellationTokenSource();
        _discoveryCts = cts;
        var generation = ++_discoveryGeneration;
        var ct = cts.Token;

        foreach (var old in Items)
            old.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        _seenKeys.Clear();

        IsLoading = true;
        StatusMessage = "";

        try
        {
            await foreach (var vm in DiscoverAsync(ct))
            {
                if (generation != _discoveryGeneration)
                {
                    return;
                }

                var key = !string.IsNullOrEmpty(vm.Item.PackageFullName) ? vm.Item.PackageFullName : vm.Name;
                if (!_seenKeys.Add(key))
                {
                    continue;
                }

                vm.PropertyChanged += OnItemPropertyChanged;
                Items.Add(vm);
                OnPropertyChanged(nameof(DiscoveryStatusText));
            }

            if (generation != _discoveryGeneration)
            {
                return;
            }

            SortItemsOnce();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer LoadAsync call, or the CancellationTokenSource was disposed
            // during teardown - not a real failure, nothing to report.
        }
        catch (Exception ex)
        {
            if (generation == _discoveryGeneration)
            {
                StatusMessage = $"Failed to load: {ex.Message}";
            }
        }
        finally
        {
            if (generation == _discoveryGeneration)
            {
                IsLoading = false;
                NotifySelectionChanged();
            }
        }
    }

    // One deterministic sort, once, when discovery finishes - never on every discovered item
    // (which would make the list visibly jump around mid-scan). Items.Move (not a clear+re-add)
    // so already-bound InstalledItemViewModel instances - and their IsSelected state - are simply
    // repositioned, never recreated.
    private void SortItemsOnce()
    {
        var sortedNames = Items
            .Select((vm, index) => (vm, index))
            .OrderBy(x => x.vm.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.vm)
            .ToList();

        for (var targetIndex = 0; targetIndex < sortedNames.Count; targetIndex++)
        {
            var currentIndex = Items.IndexOf(sortedNames[targetIndex]);
            if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledItemViewModel.IsSelected))
            NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        UninstallCommand.RaiseCanExecuteChanged();
    }

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

        // No modal of any kind between clicking "Uninstall Selected" and the batch actually
        // starting - selecting items and clicking the button is the technician's confirmation,
        // full stop. This previously included a restore-point Yes/No/Cancel offer (and a second
        // "continue anyway?" prompt on restore-point failure) - both removed per this pass's hard
        // "no modal before uninstall starts" requirement, not softened into an automatic
        // background restore-point attempt: wiring that safely into the existing whole-batch
        // UninstallService.EnterToolkitOperation() lease was judged more architectural complexity
        // than this pass should take on, and omitting it was explicitly acceptable. Revo's own
        // /wa path may still create its own restore point per its own configuration - Support
        // Tool just doesn't depend on that, and doesn't attempt one itself. The batch begins
        // immediately in both LIVE and Dry Run, identically to how Install already works.
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
            var summary = new BatchResultSummary
            {
                Title = "Dry-Run Complete",
                Counts = [new ResultCountLine("Simulated", results.Count)],
            };
            new BatchResultDialog(summary) { Owner = Application.Current.MainWindow }.ShowDialog();
            return;
        }

        var succeeded = results.Where(r => r.Status == UninstallStatus.Succeeded).ToList();
        var warned    = results.Where(r => r.Status == UninstallStatus.SucceededWithCleanupWarning).ToList();
        var protected_ = results.Where(r => r.Status == UninstallStatus.SkippedProtected).ToList();
        var started   = results.Where(r => r.Status == UninstallStatus.Started).ToList();
        var failed    = results.Where(r => r.Status == UninstallStatus.Failed).ToList();
        var skipped   = results.Where(r => r.Status == UninstallStatus.Skipped).ToList();
        var cancelled = results.Where(r => r.Status == UninstallStatus.Cancelled).ToList();

        // Warnings are supplemental, never double-counted as a second "Removed" - an item is
        // either plain Succeeded (counted in Removed) or SucceededWithCleanupWarning (counted in
        // both Removed AND Warnings, since it genuinely is both: removed, with a caveat worth
        // flagging at a glance).
        var counts = new List<ResultCountLine>();
        var removedCount = succeeded.Count + warned.Count;
        if (removedCount > 0) counts.Add(new("Removed", removedCount));
        if (warned.Count    > 0) counts.Add(new("Warnings", warned.Count));
        if (started.Count   > 0) counts.Add(new("Uninstaller started", started.Count));
        if (failed.Count    > 0) counts.Add(new("Failed", failed.Count));
        var skippedTotal = skipped.Count + protected_.Count;
        if (skippedTotal > 0) counts.Add(new("Skipped", skippedTotal));
        if (cancelled.Count > 0) counts.Add(new("Cancelled", cancelled.Count));

        var details = new List<string>();
        foreach (var r in warned) details.Add($"{r.Item.Name} — {r.ErrorMessage ?? "Cleanup warning"}");
        foreach (var r in protected_) details.Add($"{r.Item.Name} — Protected Windows component");
        foreach (var r in failed) details.Add($"{r.Item.Name} — {r.ErrorMessage ?? "Unknown error"}");
        foreach (var r in skipped) details.Add($"{r.Item.Name} — {r.ErrorMessage ?? "Skipped"}");
        foreach (var r in started) details.Add($"{r.Item.Name} — {r.ErrorMessage ?? "Uninstaller started — completion could not be verified"}");

        var resultSummary = new BatchResultSummary
        {
            Title = "Uninstall Complete",
            Counts = counts,
            Details = details,
            // Anything short of a perfectly clean run gets the warning visual - a cleanup
            // warning or a protected/skipped/started item is still worth a second glance, even
            // though none of those are a hard failure.
            HasIssues = failed.Count > 0 || warned.Count > 0 || skippedTotal > 0 || started.Count > 0,
        };
        new BatchResultDialog(resultSummary) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
}
