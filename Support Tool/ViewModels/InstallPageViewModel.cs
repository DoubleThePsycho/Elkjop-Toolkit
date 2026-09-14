using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Support_Tool.Models;
using Support_Tool.Services;

namespace Support_Tool.ViewModels;

// Distinct full-page states for the Install page's own load sequence - WinGet preparation used to
// share the same generic "Checking available applications..." spinner and the small footer's
// StatusMessage, which meant a WinGet repair (or its failure) showed up as confusing/inconsistent
// text in the wrong place, and a failed repair left an empty catalog with no clear explanation.
// Each state maps to one full-page visual in InstallPage.xaml; PrerequisiteFailed/LoadFailed both
// use the same centered failure panel with a Retry action, distinguished only by heading text.
// CatalogReady now covers the WHOLE time the grid is visible, including while apps are still being
// discovered incrementally (see IsPopulatingCatalog) - there is deliberately no separate
// "populating" full-page state, since the whole point is that the grid itself never blocks.
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
    private bool _isPopulatingCatalog;
    private string _statusMessage = "";
    private string _loadErrorHeading = "";
    private string? _loadErrorMessage;
    private string _prerequisiteDetailText = "";
    private int _totalItems;
    private int _completedItems;
    private string _currentItemName = "";
    private double _currentItemPercent;
    private string? _completionSummaryText;
    private bool _completionHasIssues;
    private IReadOnlyList<string> _completionFailureDetails = [];

    // Capped scrolling detail log for the "Preparing WinGet" panel - real streamed output from
    // WingetPrerequisiteService (its own "STAGE: ..." markers plus Repair-WinGetPackageManager's
    // cleaned verbose lines), not the coarse three-phase StatusMessage heading. Bounded so a long
    // repair can't grow this without limit; the complete output always still goes to Logger's own
    // Verbose.log regardless of what's kept here.
    private const int MaxPrerequisiteDetailLines = 100;
    private readonly List<string> _prerequisiteDetailLines = [];

    // Reentrancy guard - a second navigation while a load is still in flight awaits that same
    // in-progress attempt instead of starting a redundant/overlapping one. Same shape as
    // SettingsPageViewModel's fix for the identical class of bug - see its own remarks for why
    // _loadTask's lifecycle must be owned entirely by the LoadAsync wrapper, never cleared from
    // inside the inner load method itself (a synchronous-completion race otherwise latches a
    // stale, dead task in permanently).
    private Task? _loadTask;

    // Bumped on every LoadCoreAsync call - lets an in-flight discovery notice it has been
    // superseded (a Retry after a load failure) and stop mutating state on behalf of a load
    // nothing is showing anymore, same idea as InstalledItemsPageViewModel's own generation token.
    // Also guards the fire-and-forget background icon fetches spawned per item.
    private int _loadGeneration;

    // The most recently applied preset's desired WingetIds (see ApplyPresetSelection) - stays
    // active for the WHOLE REST of loading, not removed the moment one matching row appears. This
    // is deliberate: a single desired WingetId can legitimately match MORE than one catalog row
    // (e.g. Java 8's x86/x64 rows share one WingetId), and every one of those rows must be
    // selected regardless of which one happens to stream in first. An earlier version of this
    // logic dropped an ID from the pending set as soon as ANY already-loaded row matched it,
    // which meant a later-arriving row sharing that same ID (x64 arriving after x86 was already
    // selected) was silently never selected - fixed by never removing an ID here; only
    // _matchedPresetWingetIds (below) tracks per-ID match state, and both are cleared together
    // once nothing more can possibly stream in (see ReportUnmatchedPresetIdsAndClearTracking).
    private readonly HashSet<string> _activePresetWingetIds = new(StringComparer.OrdinalIgnoreCase);

    // Which of _activePresetWingetIds have matched at least one loaded row so far - used only to
    // report genuinely-unmatched IDs once loading finishes (or immediately, if the catalog was
    // already fully loaded when the preset was applied). Never used to decide whether to keep
    // selecting further rows for an ID - see _activePresetWingetIds's own remarks.
    private readonly HashSet<string> _matchedPresetWingetIds = new(StringComparer.OrdinalIgnoreCase);

    // True from the moment ApplyPresetSelection runs until tracking is cleared (see
    // ReportUnmatchedPresetIdsAndClearTracking) - NOT the same thing as "_activePresetWingetIds is
    // non-empty" (a degenerate zero-app preset must still deselect every future row, not silently
    // fall back to App.Preselected). While true, ApplyPendingSelectionTo explicitly selects OR
    // deselects every row that streams in, per ApplyPresetSelection's own "replaces the current
    // selection with exactly the desired set" contract - a later-arriving row with
    // App.Preselected=true that ISN'T part of the active preset must end up deselected, not left
    // selected by default. While false (no preset has been applied, or none is active right now),
    // a streaming row keeps its normal InstallableAppViewModel-constructor default from
    // App.Preselected untouched.
    private bool _isPresetActive;

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

    // True for the whole time apps are still streaming into an already-visible catalog grid - a
    // small non-blocking indicator (mirrors InstalledItemsPageViewModel's own DiscoveryStatusText
    // pattern), never a full-page state: already-added cards stay fully usable/selectable while
    // this is true.
    public bool IsPopulatingCatalog
    {
        get => _isPopulatingCatalog;
        private set
        {
            if (SetProperty(ref _isPopulatingCatalog, value))
            {
                OnPropertyChanged(nameof(CatalogPopulationStatusText));
            }
        }
    }

    public string CatalogPopulationStatusText => IsPopulatingCatalog
        ? $"Loading applications… {Categories.Sum(c => c.Items.Count)} found"
        : "";

    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetProperty(ref _isInstalling, value);
    }

    // Real batch progress - an honest numerator/denominator (the selected item count is known
    // up front and never changes mid-batch), not an animated/invented percentage. Fed by
    // InstallService's own InstallBatchProgress reports (see InstallAsync below); a failed item
    // still advances CompletedItems exactly like a succeeded one, so the batch always reaches
    // TotalItems once every selected item has been processed one way or another.
    public int TotalItems
    {
        get => _totalItems;
        private set
        {
            if (SetProperty(ref _totalItems, value))
            {
                OnPropertyChanged(nameof(InstallProgressPercent));
                OnPropertyChanged(nameof(InstallProgressText));
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
                OnPropertyChanged(nameof(InstallProgressPercent));
                OnPropertyChanged(nameof(InstallProgressText));
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
                OnPropertyChanged(nameof(InstallProgressText));
            }
        }
    }

    // Real intra-item progress (0-100) for whichever item is currently running - fed by
    // InstallService's own InstallBatchProgress.CurrentItemPercent reports, which in turn come
    // from RealInstallExecutor's live winget parsing (or stay at 0 for Dry Run/an item with no
    // trustworthy signal yet). Reset to 0 at the start of every item (see the batchProgress
    // handler in InstallAsync below) so it never bleeds a finished item's value into the next one.
    public double CurrentItemPercent
    {
        get => _currentItemPercent;
        private set
        {
            if (SetProperty(ref _currentItemPercent, value))
            {
                OnPropertyChanged(nameof(InstallProgressPercent));
            }
        }
    }

    // 0-100 for a determinate ProgressBar (Minimum=0/Maximum=100) - blends the fully-completed
    // item count with however far the currently-running item has real progress into, so the bar
    // advances continuously (e.g. 5 done + current item 70% = 57%) instead of only moving once per
    // whole item. When Completed == Total (the batch's final report, CurrentItemPercent = 0 by
    // construction) this still lands exactly on 100 - the fractional term contributes nothing.
    public double InstallProgressPercent => TotalItems > 0
        ? (CompletedItems + CurrentItemPercent / 100.0) / TotalItems * 100.0
        : 0;

    // Compact text for both the page's own footer and the persistent cross-page Install footer -
    // e.g. "Installing Steam - 2 of 7 complete", or once the batch finishes, "7 of 7 complete".
    public string InstallProgressText
    {
        get
        {
            if (TotalItems <= 0)
            {
                return "";
            }

            return string.IsNullOrEmpty(CurrentItemName)
                ? $"{CompletedItems} of {TotalItems} complete"
                : $"Installing {CurrentItemName} - {CompletedItems} of {TotalItems} complete";
        }
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

    // Non-modal completion state for the last finished install batch (replaces the old
    // "Install Complete" popup - see InstallAsync/SetCompletionSummary). Null means "no batch has
    // completed yet since the page loaded" - the footer shows nothing extra in that case. Cleared
    // at the start of every new batch (ClearCompletionSummary), never accumulated across runs.
    public string? CompletionSummaryText
    {
        get => _completionSummaryText;
        private set => SetProperty(ref _completionSummaryText, value);
    }

    // Drives the summary's success/warning icon treatment - true when at least one item in the
    // last batch didn't succeed.
    public bool CompletionHasIssues
    {
        get => _completionHasIssues;
        private set => SetProperty(ref _completionHasIssues, value);
    }

    // One concise "<app> — <reason>" line per failed item from the last batch - shown in a small
    // expandable disclosure next to the summary line rather than a raw WinGet dump; empty when the
    // last batch had no failures.
    public IReadOnlyList<string> CompletionFailureDetails
    {
        get => _completionFailureDetails;
        private set => SetProperty(ref _completionFailureDetails, value);
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

    // Called by Setup Home (SetupHomePageViewModel) when a technician applies "SA Standard" /
    // "SA Gaming" (plus whichever browser they picked) - replaces the current selection with
    // exactly the given set of WingetIds, keyed by stable app identity so it works correctly
    // regardless of whether this page has finished loading:
    //   - already-loaded rows: selected/deselected immediately, matched purely by WingetId (a
    //     shared WingetId, e.g. Java 8 x86/x64, selects every row that carries it - intentional).
    //   - not yet loaded: every desired ID stays ACTIVE for the rest of loading (not just until
    //     the first match - see _activePresetWingetIds's own remarks) and is applied to every
    //     matching row as it streams in from LoadCoreAsync (see ApplyPendingSelectionTo).
    //   - never appears at all: logged once nothing more is coming (immediately here if the
    //     catalog has already finished loading; otherwise once LoadCoreAsync itself finishes).
    // Deliberately does not introduce a second catalog model - this only ever mutates the same
    // InstallableAppViewModel instances Categories already owns.
    public void ApplyPresetSelection(IReadOnlyCollection<string> wingetIds)
    {
        var idSet = new HashSet<string>(wingetIds, StringComparer.OrdinalIgnoreCase);
        _activePresetWingetIds.Clear();
        _activePresetWingetIds.UnionWith(idSet);
        _matchedPresetWingetIds.Clear();
        _isPresetActive = true;

        foreach (var item in Categories.SelectMany(c => c.Items))
        {
            var matches = idSet.Contains(item.App.WingetId);
            item.IsSelected = matches;
            if (matches)
            {
                _matchedPresetWingetIds.Add(item.App.WingetId);
            }
        }

        // Nothing will stream in later to resolve any leftovers if loading has already finished -
        // report now instead of waiting for a load that isn't coming.
        if (HasLoaded)
        {
            ReportUnmatchedPresetIdsAndClearTracking();
        }

        NotifySelectionChanged();
    }

    // Called for every row as it streams in from LoadCoreAsync. While a preset is active
    // (_isPresetActive), it explicitly SELECTS OR DESELECTS every row - not just the ones that
    // match - so a future row with App.Preselected=true that ISN'T part of the active preset
    // can't sneak in selected just because nothing here touched it. Outside of an active preset,
    // a row keeps the normal config-driven default InstallableAppViewModel's own constructor
    // already applied from App.Preselected - this method does nothing in that case.
    private void ApplyPendingSelectionTo(InstallableAppViewModel vm)
    {
        if (!_isPresetActive)
        {
            return;
        }

        var matches = _activePresetWingetIds.Contains(vm.App.WingetId);
        vm.IsSelected = matches;
        if (matches)
        {
            _matchedPresetWingetIds.Add(vm.App.WingetId);
        }
    }

    // Reports any active preset WingetIds that matched ZERO catalog rows, then clears ALL preset
    // tracking state (including _isPresetActive) - called once discovery genuinely has nothing
    // left to offer (end of LoadCoreAsync, or immediately from ApplyPresetSelection when the
    // catalog was already fully loaded). An ID that already matched at least one row is never
    // reported here even if more of that same ID could theoretically still be pending - matching
    // once is sufficient proof it exists in the catalog. Always clears every field, even for a
    // degenerate zero-ID preset, so no stale state survives past the load it applied to.
    private void ReportUnmatchedPresetIdsAndClearTracking()
    {
        if (_activePresetWingetIds.Count > 0)
        {
            var unmatched = _activePresetWingetIds
                .Except(_matchedPresetWingetIds, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unmatched.Count > 0)
            {
                _logger.Warn($"Setup preset referenced WingetId(s) not found in the Install catalog: {string.Join(", ", unmatched)}");
            }
        }

        _activePresetWingetIds.Clear();
        _matchedPresetWingetIds.Clear();
        _isPresetActive = false;
    }

    private async Task LoadCoreAsync()
    {
        var generation = ++_loadGeneration;

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
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var detailProgress = new Progress<string>(AppendPrerequisiteDetailLine);
            var readiness = await _wingetPrerequisite.EnsureReadyAsync(ViewState.IsLiveMode, ViewState, _logger, progress, detailProgress);

            if (generation != _loadGeneration)
            {
                return;
            }

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

            // The grid itself becomes visible immediately here - categories/items stream in below
            // one at a time rather than the technician staring at a blocking full-page spinner for
            // the whole catalog scan. IsPopulatingCatalog drives a small non-blocking indicator
            // only, never a full-page state.
            LoadState = InstallPageLoadState.CatalogReady;
            IsPopulatingCatalog = true;
            StatusMessage = "";

            foreach (var category in await _catalog.GetCatalogAsync())
            {
                if (generation != _loadGeneration)
                {
                    return;
                }

                InstallCategoryViewModel? categoryVm = null;
                var categoryAdded = false;

                foreach (var app in category.Apps)
                {
                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    AppInstallState state;
                    try
                    {
                        state = await _statusChecker.GetStateAsync(app.WingetId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to check status for '{app.Name}': {ex.Message}");
                        state = AppInstallState.NotInstalled;
                    }

                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    // Cache-only, synchronous - never blocks this card's own appearance on a
                    // network round-trip. A genuine cache miss is fetched in the background below
                    // and applied to this exact instance once/if it resolves.
                    var cachedIconPath = _iconCache.TryGetCachedIconPath(app.WingetId);
                    var vm = new InstallableAppViewModel(app, state, cachedIconPath);
                    vm.PropertyChanged += OnItemPropertyChanged;
                    ApplyPendingSelectionTo(vm);

                    categoryVm ??= new InstallCategoryViewModel(category.Name, [], category.ShowSelectAll);
                    if (!categoryAdded)
                    {
                        Categories.Add(categoryVm);
                        categoryAdded = true;
                    }

                    categoryVm.AddItem(vm);
                    OnPropertyChanged(nameof(CatalogPopulationStatusText));
                    NotifySelectionChanged();

                    if (cachedIconPath is null && !string.IsNullOrWhiteSpace(app.Link))
                    {
                        _ = FetchIconInBackgroundAsync(vm, app.WingetId, app.Link, app.Name, generation);
                    }
                }
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            ReportUnmatchedPresetIdsAndClearTracking();

            HasLoaded = true;
            IsPopulatingCatalog = false;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            LoadErrorHeading = "Could not load applications";
            LoadErrorMessage = ex.Message;
            LoadState = InstallPageLoadState.LoadFailed;
            IsPopulatingCatalog = false;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                NotifySelectionChanged();
            }
        }
    }

    // Fire-and-forget from the discovery loop above - a genuine icon-cache miss fetches over the
    // network (AppIconCacheService.GetOrFetchIconPathAsync), which must never block that item's
    // own card from appearing. Runs to completion independently of the loop that spawned it;
    // guarded by the same generation token so a superseded load's stray fetch can't mutate a VM
    // instance a newer load has already discarded.
    private async Task FetchIconInBackgroundAsync(InstallableAppViewModel vm, string wingetId, string link, string name, int generation)
    {
        string? path;
        try
        {
            path = await _iconCache.GetOrFetchIconPathAsync(wingetId, link);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to fetch icon for '{name}': {ex.Message}");
            return;
        }

        if (path is null)
        {
            return;
        }

        // BitmapImage construction/property assignment needs to happen on the UI thread this
        // ViewModel's bound controls live on - GetOrFetchIconPathAsync's own await can resume on a
        // thread-pool thread.
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (generation == _loadGeneration)
            {
                vm.Icon = InstallableAppViewModel.ToImageSource(path);
            }
        });
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

        // Starting a new batch always replaces whatever completion summary the previous one left
        // behind (goal 18) - never stacks/accumulates across runs.
        ClearCompletionSummary();

        IsInstalling = true;
        TotalItems = selected.Count;
        CompletedItems = 0;
        CurrentItemName = "";
        CurrentItemPercent = 0;
        var progress = new Progress<string>(msg => StatusMessage = msg);
        var batchProgress = new Progress<InstallBatchProgress>(p =>
        {
            CompletedItems = p.Completed;
            TotalItems = p.Total;
            CurrentItemName = p.CurrentItemName;
            CurrentItemPercent = p.CurrentItemPercent;
        });

        List<InstallResult> results;
        try
        {
            results = await _installService.InstallAsync(
                selected.Select(x => (x.App, x.State)), ViewState.ExecutionMode, progress, batchProgress);
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

        // Targeted refresh only (goal 2) - never a full "HasLoaded=false; await LoadAsync()"
        // reload. Only the apps that actually participated in this batch get their status
        // re-checked, mutating their EXISTING InstallableAppViewModel instances in place; every
        // other card, the category structure, icons, and scroll position are all left completely
        // undisturbed.
        await RefreshParticipantsAfterBatchAsync(selected, results);

        // Inline, non-modal completion state (goal 1/18) - no "Install Complete" popup.
        SetCompletionSummary(results);
    }

    private async Task RefreshParticipantsAfterBatchAsync(List<InstallableAppViewModel> participants, List<InstallResult> results)
    {
        if (results.Count == 0 || results[0].Status == InstallOutcome.DryRun)
        {
            // Nothing on the machine actually changed during a Dry Run - re-checking winget would
            // just waste time and can never disagree with what's already shown.
            return;
        }

        for (var i = 0; i < participants.Count; i++)
        {
            var vm = participants[i];

            // Matched positionally, not by WingetId - InstallService.InstallAsync reports exactly
            // one result per input item in the same order it received them, and two catalog rows
            // may legitimately share one WingetId (e.g. Java 8 x86/x64), which a WingetId-keyed
            // lookup could not disambiguate.
            var succeeded = i < results.Count && results[i].Status == InstallOutcome.Succeeded;

            try
            {
                vm.State = await _statusChecker.GetStateAsync(vm.App.WingetId);
            }
            catch (Exception ex)
            {
                // Retain whatever State already was rather than guessing - the technician can
                // still see/retry this item, it just won't reflect the very latest check.
                _logger.Warn($"Failed to refresh status for '{vm.Name}' after install: {ex.Message}");
            }

            // Succeeded -> deselect (job done, matches the row's own now-current state). Failed ->
            // keep selected so retrying is one more click on the same button, no need to re-find
            // and re-check the item first.
            if (succeeded)
            {
                vm.IsSelected = false;
            }
        }

        NotifySelectionChanged();
    }

    private void SetCompletionSummary(List<InstallResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        if (results[0].Status == InstallOutcome.DryRun)
        {
            CompletionSummaryText = $"Dry run complete — {results.Count} simulated.";
            CompletionHasIssues = false;
            CompletionFailureDetails = [];
            return;
        }

        var succeededCount = results.Count(r => r.Status == InstallOutcome.Succeeded);
        var failed = results.Where(r => r.Status != InstallOutcome.Succeeded).ToList();

        CompletionSummaryText = failed.Count == 0
            ? $"{succeededCount} application{(succeededCount == 1 ? "" : "s")} installed/updated."
            : $"{succeededCount} installed/updated · {failed.Count} failed";
        CompletionHasIssues = failed.Count > 0;
        CompletionFailureDetails = failed
            .Select(r => $"{r.App.Name} — {r.ErrorMessage ?? "Unknown error"}")
            .ToList();
    }

    private void ClearCompletionSummary()
    {
        CompletionSummaryText = null;
        CompletionHasIssues = false;
        CompletionFailureDetails = [];
    }
}
