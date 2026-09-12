using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using Support_Tool.Models;
using Support_Tool.Services;

namespace Support_Tool.ViewModels;

public class InstallPageViewModel : ViewModelBase
{
    private readonly InstallCatalogService _catalog;
    private readonly WingetStatusChecker _statusChecker;
    private readonly InstallService _installService;
    private readonly AppIconCacheService _iconCache;
    private readonly WingetPrerequisiteService _wingetPrerequisite;
    private readonly Logger _logger = new();
    private bool _isLoading;
    private bool _isInstalling;
    private string _statusMessage = "";

    // Reentrancy guard - a second navigation (or the post-install refresh) while a load is still
    // in flight awaits that same in-progress attempt instead of starting a redundant/overlapping
    // one. Same shape as SettingsPageViewModel's fix for the identical class of bug - see its own
    // remarks for why _loadTask's lifecycle must be owned entirely by the LoadAsync wrapper, never
    // cleared from inside the inner load method itself (a synchronous-completion race otherwise
    // latches a stale, dead task in permanently).
    private Task? _loadTask;

    public InstallPageViewModel(
        InstallCatalogService catalog,
        WingetStatusChecker statusChecker,
        InstallService installService,
        AppIconCacheService iconCache,
        WingetPrerequisiteService wingetPrerequisite,
        SharedViewState viewState)
    {
        _catalog = catalog;
        _statusChecker = statusChecker;
        _installService = installService;
        _iconCache = iconCache;
        _wingetPrerequisite = wingetPrerequisite;
        ViewState = viewState;

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedCount > 0);
    }

    public SharedViewState ViewState { get; }
    public ObservableCollection<InstallCategoryViewModel> Categories { get; } = new();
    public AsyncRelayCommand InstallCommand { get; }

    // Means "successfully loaded" (WinGet prerequisite ready AND catalog enumerated), not merely
    // "a load was attempted" - a failed prerequisite check (broken/ancient WinGet) must not
    // permanently latch this true with an empty/false Categories collection, or a later
    // navigation would never retry. See LoadCoreAsync's own remarks.
    public bool HasLoaded { get; private set; }

    public int SelectedCount => Categories.SelectMany(c => c.Items).Count(x => x.IsSelected);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetProperty(ref _isInstalling, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task LoadAsync()
    {
        if (HasLoaded)
        {
            return;
        }

        var task = _loadTask;
        if (task is null)
        {
            task = LoadCoreAsync();
            _loadTask = task;
        }

        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_loadTask, task))
            {
                _loadTask = null;
            }
        }
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;

        foreach (var category in Categories)
            foreach (var item in category.Items)
                item.PropertyChanged -= OnItemPropertyChanged;
        Categories.Clear();

        try
        {
            // WinGet readiness is established BEFORE any catalog/status enumeration - the status
            // checker itself shells out to `winget list ... --disable-interactivity`, so running
            // it against a missing/ancient/crashing WinGet would either throw or (worse) quietly
            // report every single app as NotInstalled, which is indistinguishable from "genuinely
            // not installed" to the technician. allowRepair is gated on the actual execution mode
            // (not a #if DEBUG check) - a DEBUG tester who has explicitly toggled Live mode gets
            // real repair, but DEBUG Dry Run never mutates WinGet as a side effect of opening this
            // page.
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var readiness = await _wingetPrerequisite.EnsureReadyAsync(ViewState.IsLiveMode, ViewState, _logger, progress);

            if (!readiness.IsReady)
            {
                // Stop cleanly - HasLoaded stays false (never set true above), so a later
                // navigation to this page retries from scratch rather than being permanently
                // blocked, and Categories stays empty rather than filling with false
                // NotInstalled states caused by a broken WinGet.
                StatusMessage = readiness.ErrorMessage ?? "WinGet could not be prepared. Please investigate manually.";
                _logger.Error($"Install page load aborted: WinGet is not ready. {StatusMessage}");
                return;
            }

            StatusMessage = "Checking available applications...";

            foreach (var category in await _catalog.GetCatalogAsync())
            {
                var itemVms = new List<InstallableAppViewModel>();
                foreach (var app in category.Apps)
                {
                    var state = await _statusChecker.GetStateAsync(app.WingetId);
                    var iconPath = await _iconCache.GetOrFetchIconPathAsync(app.WingetId, app.Link);
                    var vm = new InstallableAppViewModel(app, state, iconPath);
                    vm.PropertyChanged += OnItemPropertyChanged;
                    itemVms.Add(vm);
                }
                Categories.Add(new InstallCategoryViewModel(category.Name, itemVms, category.ShowSelectAll));
            }

            HasLoaded = true;
            StatusMessage = "";
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

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallableAppViewModel.IsSelected))
            NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        InstallCommand.RaiseCanExecuteChanged();
    }

    private async Task InstallAsync()
    {
        var selected = Categories
            .SelectMany(c => c.Items)
            .Where(x => x.IsSelected)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "No applications are selected.",
                "Nothing Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (ViewState.IsLiveMode)
        {
            var names = string.Join("\n", selected.Select(i => $"• {i.Name}"));
            var confirm = MessageBox.Show(
                $"You are about to install/update {selected.Count} application(s):\n\n{names}\n\nContinue?",
                "Confirm Install",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            // Defense-in-depth: WinGet could in principle have been damaged/removed between page
            // load and this click. A cheap re-check is enough when it still passes (no repair run
            // twice for nothing); only if it no longer passes does this repair again, verify, and
            // only then let the batch proceed - keeping the actual RealInstallExecutor responsible
            // for exactly one package invocation each, never duplicating prerequisite logic there.
            if (!await _wingetPrerequisite.QuickCheckAsync(_logger))
            {
                StatusMessage = "Repairing WinGet...";
                var repairProgress = new Progress<string>(msg => StatusMessage = msg);
                var readiness = await _wingetPrerequisite.EnsureReadyAsync(allowRepair: true, ViewState, _logger, repairProgress);
                StatusMessage = "";

                if (!readiness.IsReady)
                {
                    MessageBox.Show(
                        $"WinGet could not be prepared for installing:\n\n{readiness.ErrorMessage}",
                        "WinGet Not Ready",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }
        }
        else
        {
            var confirm = MessageBox.Show(
                $"[DRY RUN]\n\nYou are about to simulate installing/updating {selected.Count} application(s).\n\n" +
                "No software will be installed.\n\nContinue?",
                "Confirm Dry Run",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        IsInstalling = true;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        List<InstallResult> results;
        try
        {
            results = await _installService.InstallAsync(
                selected.Select(x => (x.App, x.State)), ViewState.ExecutionMode, progress);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Install operation failed unexpectedly:\n{ex.Message}",
                "Install Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = "";
            return;
        }
        finally
        {
            IsInstalling = false;
        }

        StatusMessage = "";
        ShowResultSummary(results);

        // Forces a genuine refresh (prerequisite + catalog) rather than the no-op LoadAsync()
        // would otherwise be once HasLoaded is already true - this is the one place a reload is
        // actually wanted after the initial page load.
        HasLoaded = false;
        await LoadAsync();
    }

    private void ShowResultSummary(List<InstallResult> results)
    {
        bool isDryRun = results.Any(r => r.Status == InstallOutcome.DryRun);

        if (isDryRun)
        {
            MessageBox.Show(
                $"Dry-run complete.\n\n{results.Count} item(s) would have been processed.\n" +
                "No real software was installed.",
                "Dry-Run Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var succeeded = results.Where(r => r.Status == InstallOutcome.Succeeded).ToList();
        var failed    = results.Where(r => r.Status == InstallOutcome.Failed).ToList();

        var sb = new StringBuilder();
        if (succeeded.Count > 0) sb.AppendLine($"Installed/updated: {succeeded.Count}");
        if (failed.Count    > 0) sb.AppendLine($"Failed:            {failed.Count}");

        if (failed.Count > 0)
        {
            sb.AppendLine("\nDetails:");
            foreach (var r in failed)
                sb.AppendLine($"  • {r.App.Name}: {r.ErrorMessage ?? "Unknown error"}");
        }

        var icon = failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        MessageBox.Show(sb.ToString().Trim(), "Install Complete", MessageBoxButton.OK, icon);
    }
}
