using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using Support_Tool.Models;
using Support_Tool.Services;

namespace Support_Tool.ViewModels;

// Distinct full-page states for the Install page's own load sequence - WinGet preparation used to
// share the same generic "Checking available applications..." spinner and the small footer's
// StatusMessage, which meant a WinGet repair (or its failure) showed up as confusing/inconsistent
// text in the wrong place, and a failed repair left an empty catalog with no clear explanation.
// Each state maps to one full-page visual in InstallPage.xaml; PrerequisiteFailed/LoadFailed both
// use the same centered failure panel with a Retry action, distinguished only by heading text.
public enum InstallPageLoadState
{
    PreparingPrerequisite,
    LoadingCatalog,
    LoadFailed,
    CatalogReady,
}

public class InstallPageViewModel : ViewModelBase
{
    private readonly InstallCatalogService _catalog;
    private readonly WingetStatusChecker _statusChecker;
    private readonly InstallService _installService;
    private readonly AppIconCacheService _iconCache;
    private readonly WingetPrerequisiteService _wingetPrerequisite;
    private readonly Logger _logger = new();
    private InstallPageLoadState _loadState = InstallPageLoadState.PreparingPrerequisite;
    private bool _isInstalling;
    private string _statusMessage = "";
    private string _loadErrorHeading = "";
    private string? _loadErrorMessage;
    private string _prerequisiteDetailText = "";

    // Capped scrolling detail log for the "Preparing WinGet" panel - real streamed output from
    // WingetPrerequisiteService (its own "STAGE: ..." markers plus Repair-WinGetPackageManager's
    // cleaned verbose lines), not the coarse three-phase StatusMessage heading. Bounded so a long
    // repair can't grow this without limit; the complete output always still goes to Logger's own
    // Verbose.log regardless of what's kept here.
    private const int MaxPrerequisiteDetailLines = 100;
    private readonly List<string> _prerequisiteDetailLines = [];

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
        RetryCommand = new AsyncRelayCommand(LoadAsync);
    }

    public SharedViewState ViewState { get; }
    public ObservableCollection<InstallCategoryViewModel> Categories { get; } = new();
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }

    // Means "successfully loaded" (WinGet prerequisite ready AND catalog enumerated), not merely
    // "a load was attempted" - a failed prerequisite check (broken/ancient WinGet) must not
    // permanently latch this true with an empty/false Categories collection, or a later
    // navigation would never retry. See LoadCoreAsync's own remarks.
    public bool HasLoaded { get; private set; }

    public int SelectedCount => Categories.SelectMany(c => c.Items).Count(x => x.IsSelected);

    public InstallPageLoadState LoadState
    {
        get => _loadState;
        private set
        {
            if (SetProperty(ref _loadState, value))
            {
                OnPropertyChanged(nameof(IsPreparingPrerequisite));
                OnPropertyChanged(nameof(IsLoadingCatalog));
                OnPropertyChanged(nameof(IsLoadFailed));
                OnPropertyChanged(nameof(IsCatalogReady));
            }
        }
    }

    public bool IsPreparingPrerequisite => LoadState == InstallPageLoadState.PreparingPrerequisite;
    public bool IsLoadingCatalog => LoadState == InstallPageLoadState.LoadingCatalog;
    public bool IsLoadFailed => LoadState == InstallPageLoadState.LoadFailed;
    public bool IsCatalogReady => LoadState == InstallPageLoadState.CatalogReady;

    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetProperty(ref _isInstalling, value);
    }

    // The footer's compact progress text during an actual install batch - unrelated to page-load
    // states above, which have their own dedicated full-page panels instead of sharing this.
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // "WinGet could not be prepared" for a prerequisite failure, or a more generic heading for a
    // catalog-fetch failure that happens after WinGet was already confirmed ready.
    public string LoadErrorHeading
    {
        get => _loadErrorHeading;
        private set => SetProperty(ref _loadErrorHeading, value);
    }

    public string? LoadErrorMessage
    {
        get => _loadErrorMessage;
        private set => SetProperty(ref _loadErrorMessage, value);
    }

    // Read-only, newline-joined view of the last MaxPrerequisiteDetailLines detail lines - bound
    // directly to the "Preparing WinGet" panel's scrolling log.
    public string PrerequisiteDetailText
    {
        get => _prerequisiteDetailText;
        private set => SetProperty(ref _prerequisiteDetailText, value);
    }

    private void AppendPrerequisiteDetailLine(string line)
    {
        _prerequisiteDetailLines.Add(line);
        if (_prerequisiteDetailLines.Count > MaxPrerequisiteDetailLines)
        {
            _prerequisiteDetailLines.RemoveAt(0);
        }

        PrerequisiteDetailText = string.Join(Environment.NewLine, _prerequisiteDetailLines);
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
        LoadState = InstallPageLoadState.PreparingPrerequisite;
        LoadErrorMessage = null;
        _prerequisiteDetailLines.Clear();
        PrerequisiteDetailText = "";

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
            //
            // progress drives the dedicated "Preparing WinGet" panel's prominent heading and
            // tracks the REAL current stage - WingetPrerequisiteService itself reports each of its
            // own "STAGE: ..." markers here too (not just its own three coarse bookend messages),
            // so the heading actually advances through "Checking WinGet..." -> "Installing NuGet
            // provider..." -> "Installing WinGet repair module..." -> "Repairing Windows Package
            // Manager..." -> "Verifying WinGet..." rather than freezing at a generic "Repairing
            // WinGet..." while only the detail log below it moves. detailProgress feeds that same
            // panel's scrolling log with the real-time streamed repair output. Each message is
            // reported to exactly one or both channels by WingetPrerequisiteService itself (see
            // its own ReportStage) - this lambda pair is a plain pass-through, not a second place
            // that decides what goes where, so a message can never end up duplicated in the log.
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var detailProgress = new Progress<string>(AppendPrerequisiteDetailLine);
            var readiness = await _wingetPrerequisite.EnsureReadyAsync(ViewState.IsLiveMode, ViewState, _logger, progress, detailProgress);

            if (!readiness.IsReady)
            {
                // Stop cleanly - HasLoaded stays false (never set true above), so a later
                // navigation/Retry to this page retries from scratch rather than being
                // permanently blocked, and Categories stays empty rather than filling with false
                // NotInstalled states caused by a broken WinGet. No footer, no app cards - the
                // dedicated failure panel is the only thing shown.
                LoadErrorHeading = "WinGet could not be prepared";
                LoadErrorMessage = readiness.ErrorMessage ?? "WinGet could not be prepared. Please investigate manually.";
                LoadState = InstallPageLoadState.LoadFailed;
                _logger.Error($"Install page load aborted: WinGet is not ready. {LoadErrorMessage}");
                return;
            }

            LoadState = InstallPageLoadState.LoadingCatalog;
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
            LoadState = InstallPageLoadState.CatalogReady;
        }
        catch (Exception ex)
        {
            LoadErrorHeading = "Could not load applications";
            LoadErrorMessage = ex.Message;
            LoadState = InstallPageLoadState.LoadFailed;
        }
        finally
        {
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

        // No confirmation dialog - clicking "Install Selected" begins immediately in both LIVE
        // and Dry Run, in the same direct-action way for both. The page/button itself already
        // communicates what the action does; a second "are you sure?" prompt was redundant.
        if (ViewState.IsLiveMode)
        {
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
