using System.Collections.ObjectModel;
using System.Windows;
using Support_Tool.Services;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool.ViewModels;

public enum WindowsUpdatePageState
{
    Idle,
    CheckingConnectivity,
    WaitingForConnection,
    NoConnection,
    Running,
    RebootRequired,
    FullyUpToDate,
    StillPending,
    Failed,
}

// Presentation wrapper around the engine's UpdateStatusEntry (internal to Services, so this
// keeps the ViewModel/XAML-facing shape separate from the engine's own record) - mirrors
// UpdateProgressWindow.xaml.cs's DescribeState/DescribePercent from the pre-migration Setup
// Agent UI.
public sealed class UpdateRowViewModel
{
    public UpdateRowViewModel(UpdateStatusEntry entry)
    {
        Name = entry.Name;

        // Presentation-only distinction (external review, real-hardware testing) - WUA can sit in
        // Downloading with PercentComplete null/0 for a real, sometimes-lengthy stretch before it
        // reports any trustworthy measurable progress (this is genuine WUA behavior, not something
        // this pass changes - see WindowsUpdateProgressCallback.BuildDownloadEntries, untouched).
        // Showing "Downloading / 0%" during that stretch reads as frozen; "Preparing download…" /
        // "-" is honest about "entered the phase, nothing measurable yet" without inventing fake
        // movement. The instant WUA reports PercentComplete > 0, this reverts to the real
        // "Downloading" + percent - same data, just not misrepresented as already-measuring.
        var isPreparingDownload = entry.State == UpdateState.Downloading && entry.PercentComplete is null or 0;

        // Phase 5: three ASCII periods (not the single Unicode ellipsis "…" this used to use) on
        // every active/in-progress state - terminal states (Downloaded/Installed/Pending restart/
        // Failed) get none, unchanged.
        StateText = isPreparingDownload ? "Preparing..." : DescribeState(entry.State);
        PercentText = isPreparingDownload ? "-" : DescribePercent(entry.PercentComplete);
    }

    public string Name { get; }
    public string StateText { get; }
    public string PercentText { get; }

    private static string DescribeState(UpdateState state) => state switch
    {
        UpdateState.Downloading => "Downloading...",
        UpdateState.Downloaded => "Downloaded",
        UpdateState.Installing => "Installing...",
        UpdateState.Installed => "Installed",
        UpdateState.Failed => "Failed",
        UpdateState.PendingRestart => "Pending restart",
        _ => "Pending...",
    };

    private static string DescribePercent(int? percent) => percent is { } p ? $"{p}%" : "-";
}

// The shared Windows Update page ViewModel - one instance, constructed once in App.xaml.cs. It's
// a real standalone page in its own right (navigated to via MainViewModel.NavigateWindowsUpdateCommand),
// used identically by the Setup workflow's mandatory gate (Support Tool navigates straight to it
// at startup when Stage=WindowsUpdate, auto-starting the check) and by Service's optional,
// technician-initiated use - there is exactly one page/header, never an embedded copy inside
// another page. Re-hosts Setup Agent's orchestration (WindowsUpdateOrchestrator) on a dedicated
// STA worker thread; this ViewModel itself only ever touches WPF-bound state from the UI thread,
// using Progress<T> to receive callbacks that may originate from the STA worker, the availability
// watcher's own background thread, or WUA's own COM callback threads. Navigating to another page
// does not cancel/reset/restart anything - the same StartAsync invocation just keeps running in
// the background; MainWindow's persistent footer (bound through MainViewModel.WindowsUpdatePage)
// is what keeps the technician informed while they're elsewhere.
//
// Phase 2A detected and displayed RebootRequired but never rebooted or persisted session state.
// Phase 2B added that lifecycle via the optional onOutcome delegate (constructed in App.xaml.cs as
// WindowsUpdateLifecycleCoordinator.HandleOutcomeAsync) - this ViewModel stays workflow-agnostic
// itself; the coordinator is the one that branches on SessionState.Workflow. The later reboot-
// countdown/defer rework added onRebootNowRequested and IsRebootDeferred, following the same
// "this ViewModel just exposes state and routes the action; the coordinator owns the actual
// persist/verify/reboot invariants" split.
public class WindowsUpdatePageViewModel : ViewModelBase
{
    private readonly IWindowsUpdateExecutor _executor;
    private readonly IWifiConnectionService _wifiService;
    private readonly ConfigService _configService;
    private readonly SharedViewState _viewState;
    private readonly Logger _logger = new();

    private WindowsUpdatePageState _state = WindowsUpdatePageState.Idle;
    private string _statusText = "";
    private string _lastActivityText = "";
    private bool _preventSleepEnabled = true;
    private bool _isBusy;
    private bool _isRebootDeferred;
    private double _overallWindowsUpdateProgressPercent;

    // Highest known contribution (0-2, see ContributionFor) seen so far THIS PASS for each update,
    // keyed by title. This dictionary's key set doubles as the pass's active-title universe - once
    // a title is seen anywhere in this pass it stays in the denominator for the rest of the pass,
    // even if a later, narrower snapshot (e.g. WindowsUpdateExecutor's own toInstall-only install
    // callbacks, once some titles from the download phase turned out not to need installing) no
    // longer mentions it; RecomputeOverallProgress only ever adds/updates entries from what it's
    // given, it never removes one just because the latest snapshot doesn't contain it (see its own
    // remarks). Retaining the highest value also means a later transient/out-of-order snapshot can
    // never walk the aggregate bar backwards within the pass (e.g. Downloading 50% momentarily
    // re-reported as Downloading 30%). The background availability watcher discovering a genuinely
    // new title (PendingUpdatesTable.MergeNewTitles) adds a new zero-contribution entry here too -
    // legitimately enlarging the denominator and lowering the aggregate percent is the one accepted
    // exception to "never decreases mid-pass", not a bug.
    //
    // Cleared only when WindowsUpdateOrchestrator.Run signals a genuine new WUA pass boundary (see
    // the onNewPassStarting callback wired up in RunOnDedicatedStaWorkerAsync/BeginNewPass) - never
    // merely because a callback's snapshot shape changed (download collection -> toInstall subset
    // is the SAME pass, not a new one).
    private readonly Dictionary<string, double> _updateContributions = new(StringComparer.OrdinalIgnoreCase);

    // Diagnostics only (external review, real-hardware testing) - lets Verbose.log answer "how
    // long did this title actually sit preparing its download before WUA reported anything
    // measurable" after the fact, without adding a second timer/watchdog: this just timestamps two
    // transitions the existing _rowsProgress callback already observes on every real progress
    // report. _downloadPhaseEnteredAt records when a title was first seen as Downloading (any
    // percent); _titlesWithMeasurableDownloadProgress records which titles have already logged
    // their first real (>0) percent, so that line is written exactly once per title per pass.
    // Both cleared at the same new-pass boundary as _updateContributions (see BeginNewPass) - a
    // title's "entered download phase" time from a previous pass must never bleed into a fresh
    // pass's own timing.
    private readonly Dictionary<string, DateTime> _downloadPhaseEnteredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _titlesWithMeasurableDownloadProgress = new(StringComparer.OrdinalIgnoreCase);

    // Phase 5, external review: the last row snapshot/status text actually rendered - used to
    // suppress both the Rows-rebuild pipeline and the "Last activity" stamp when the new 2-second
    // GetProgress poll (see WindowsUpdateExecutor) reports a snapshot that's visibly identical to
    // what's already shown. A poll occurring is not activity. Reset alongside every other
    // per-pass baseline at a genuine new-pass boundary (StartAsync's own Rows.Clear(); BeginNewPass) -
    // otherwise the first real snapshot of a NEW run could be wrongly suppressed just because it
    // happens to look like the PREVIOUS run's last-rendered state, even though Rows itself was
    // already cleared.
    private List<(string Name, UpdateState State, int? Percent)>? _lastRenderedRowSnapshot;

    // Constructed once here, on the UI thread (this ViewModel is itself constructed in
    // App.xaml.cs's OnStartup), so each Progress<T> permanently captures the UI dispatcher's
    // SynchronizationContext - Report() is then safe to call from any thread (the dedicated STA
    // worker, the availability watcher's background thread, or WUA's own COM callback threads),
    // always resuming the actual state mutation back on the UI thread.
    // Progress<T> implements IProgress<T>.Report explicitly, so it's only callable through an
    // IProgress<T>-typed reference - declared as the interface here rather than casting at each
    // call site.
    private readonly IProgress<IReadOnlyList<UpdateStatusEntry>> _rowsProgress;
    private readonly IProgress<string> _statusProgress;
    private readonly Func<WindowsUpdateOutcome, Task>? _onOutcome;
    private readonly Func<Task>? _onRebootNowRequested;

    public WindowsUpdatePageViewModel(
        IWindowsUpdateExecutor executor,
        IWifiConnectionService wifiService,
        ConfigService configService,
        SharedViewState viewState,
        Func<WindowsUpdateOutcome, Task>? onOutcome = null,
        Func<Task>? onRebootNowRequested = null)
    {
        _executor = executor;
        _wifiService = wifiService;
        _configService = configService;
        _viewState = viewState;
        _onOutcome = onOutcome;
        _onRebootNowRequested = onRebootNowRequested;

        _rowsProgress = new Progress<IReadOnlyList<UpdateStatusEntry>>(HandleRowsProgress);
        _statusProgress = new Progress<string>(HandleStatusProgress);

        // Re-raised whenever the row snapshot changes - the only thing ProgressSummaryText/
        // HasMeasurableProgress read.
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ProgressSummaryText));
            OnPropertyChanged(nameof(HasMeasurableProgress));
        };

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        // Disabled while another toolkit operation is active - belt-and-braces alongside
        // WindowsUpdateLifecycleCoordinator's own authoritative re-check right before it actually
        // reboots (see HandleRebootNowRequestedAsync); this CanExecute is a UX nicety, not the
        // safety boundary itself, since IsToolkitOperationActive can in principle flip between
        // this check and the click actually being processed.
        RebootNowCommand = new AsyncRelayCommand(RebootNowAsync, () => IsRebootDeferred && !_viewState.IsToolkitOperationActive);

        // SharedViewState.EnterToolkitOperation() can be entered/exited from whatever thread an
        // install/uninstall/debloat operation runs on - never assume this fires on the UI thread.
        // BeginInvoke is safe to call from any thread and marshals the actual property-changed
        // notification (which WPF bindings require to happen on the UI thread) back correctly.
        _viewState.ToolkitOperationActiveChanged += () =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(IsRebootWaitingForOtherOperation));
                RebootNowCommand.RaiseCanExecuteChanged();
            });
    }

    public ObservableCollection<UpdateRowViewModel> Rows { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand RebootNowCommand { get; }

    // Set once, right after construction in App.xaml.cs, from the loaded SessionState -
    // Workflow never changes for the lifetime of a Support Tool process, so this is a plain
    // settable property rather than a constructor parameter (avoids a chicken-and-egg ordering
    // problem: the session is loaded before this ViewModel exists, but assigning it here keeps
    // the constructor focused on the engine/service dependencies). Used only to pick the
    // workflow-appropriate header subtitle - this ViewModel otherwise stays workflow-agnostic,
    // matching its existing design (the coordinator is what branches on Workflow for lifecycle
    // decisions).
    public ToolkitWorkflow? Workflow { get; set; }

    public string SubHeaderText => Workflow switch
    {
        ToolkitWorkflow.Setup => "Required before setup can continue",
        _ => "Check and install available Windows updates",
    };

    // The page header's own subtitle - the static workflow-aware description before anything has
    // run (StatusText is still empty at that point), the live StatusText once a check has
    // actually started. One single header, never a second one stacked above it.
    public string HeaderSubtitleText => IsIdle ? SubHeaderText : StatusText;

    public WindowsUpdatePageState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsWaitingForConnection));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsRebootRequired));
                OnPropertyChanged(nameof(IsFullyUpToDate));
                OnPropertyChanged(nameof(IsStillPending));
                OnPropertyChanged(nameof(IsNoConnection));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(HasActiveOrPendingOperation));
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(HeaderSubtitleText));
            }
        }
    }

    public bool IsIdle              => State == WindowsUpdatePageState.Idle;
    public bool IsWaitingForConnection => State is WindowsUpdatePageState.CheckingConnectivity or WindowsUpdatePageState.WaitingForConnection;
    public bool IsRunning           => State == WindowsUpdatePageState.Running;
    public bool IsRebootRequired    => State == WindowsUpdatePageState.RebootRequired;
    public bool IsFullyUpToDate     => State == WindowsUpdatePageState.FullyUpToDate;
    public bool IsStillPending      => State == WindowsUpdatePageState.StillPending;
    public bool IsNoConnection      => State == WindowsUpdatePageState.NoConnection;
    public bool IsFailed            => State == WindowsUpdatePageState.Failed;

    // True once a terminal (non-Idle, non-in-progress) state is reached - drives the "check
    // again" affordance without needing a separate flag.
    public bool IsFinished => State is WindowsUpdatePageState.RebootRequired or WindowsUpdatePageState.FullyUpToDate
        or WindowsUpdatePageState.StillPending or WindowsUpdatePageState.NoConnection or WindowsUpdatePageState.Failed;

    // True only in the sub-case of RebootRequired where the technician cancelled the reboot
    // countdown - WindowsUpdateLifecycleCoordinator sets this (via the Page reference wired in
    // App.xaml.cs) after a cancelled countdown, and clears it before an actual reboot proceeds
    // (via the countdown expiring or "Reboot now"). Deliberately separate from State/
    // IsRebootRequired: State only tracks the outcome shape, not "did the technician defer this
    // specific reboot" - see the coordinator's own remarks.
    public bool IsRebootDeferred
    {
        get => _isRebootDeferred;
        set
        {
            if (SetProperty(ref _isRebootDeferred, value))
            {
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(IsRebootWaitingForOtherOperation));
                RebootNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // Drives the cross-page footer's visibility (MainViewModel.ShowWindowsUpdateFooter combines
    // this with "not already viewing the Windows Update page"). True for anything worth a
    // persistent reminder: an active run, a deferred reboot, or a terminal state that still needs
    // attention (StillPending/Failed/NoConnection). False once genuinely idle (never started) or
    // once FullyUpToDate is reached - which by construction means 0 eligible updates AND no
    // reboot pending, so there's nothing left to show a footer about.
    public bool HasActiveOrPendingOperation => !IsIdle && !IsFullyUpToDate;

    // True only while a reboot is deferred AND another toolkit operation (install/uninstall/
    // debloat) is genuinely why it can't proceed automatically yet - distinct from a technician-
    // cancelled countdown with nothing else running, which is just "waiting for Reboot now".
    // WindowsUpdateLifecycleCoordinator never shows the countdown at all in this case (see its
    // own remarks) - this only affects wording/messaging, not the reboot-safety decision itself
    // (SharedViewState.IsToolkitOperationActive is re-checked directly, and authoritatively, at
    // the moment "Reboot now" is actually clicked).
    public bool IsRebootWaitingForOtherOperation => IsRebootDeferred && _viewState.IsToolkitOperationActive;

    // Compact status line for the footer - "needs attention" for the states where StatusText
    // alone (e.g. a stale "Windows Update check failed: ...") would be too verbose for a small
    // strip, a distinct message while a deferred reboot is specifically waiting on another
    // toolkit operation to finish, "Restart required to continue" for an otherwise-deferred
    // reboot, StatusText otherwise (mirrors exactly what the full Windows Update page itself
    // shows while running).
    public string FooterStatusText => IsRebootDeferred
        ? (IsRebootWaitingForOtherOperation
            ? "Restart required - waiting for current operation to finish"
            : "Restart required to continue")
        : State is WindowsUpdatePageState.Failed or WindowsUpdatePageState.StillPending or WindowsUpdatePageState.NoConnection
            ? "Needs attention"
            : StatusText;

    // Derived from the existing Rows snapshot - not a second Windows Update engine/poll, just a
    // compact read of the same observable state the full page's own list already shows.
    public string ProgressSummaryText
    {
        get
        {
            if (Rows.Count == 0)
            {
                return string.Empty;
            }

            var completed = Rows.Count(r => r.StateText is "Installed" or "Pending restart");
            return $"{completed}/{Rows.Count} updates";
        }
    }

    // True once real update rows exist (a search has actually completed) - drives the overall
    // progress bar's switch from indeterminate to determinate, both on the full page and in the
    // persistent footer. False during connectivity checks/the update search itself, where there is
    // no real numeric total to show yet.
    public bool HasMeasurableProgress => Rows.Count > 0;

    // Single real aggregate WUA progress percentage, built from the same Rows/UpdateStatusEntry
    // state the table already shows - both the full page's overall bar and the persistent
    // cross-page footer bind to this exact property, so there is only ever one calculation.
    // Each update is two equal work units (download, install) per WindowsUpdatePageViewModel's
    // own remarks on RecomputeOverallProgress/ContributionFor - see those for the actual math.
    public double OverallWindowsUpdateProgressPercent
    {
        get => _overallWindowsUpdateProgressPercent;
        private set => SetProperty(ref _overallWindowsUpdateProgressPercent, value);
    }

    // Phase 5, external review: the stamp lives here, in the setter itself, rather than in
    // whichever caller happens to assign it - StatusText is assigned directly in several places
    // (StartAsync's own connectivity/Wi-Fi/WU-check-failure strings) as well as through
    // HandleStatusProgress below, and every one of those is a genuine visible status change that
    // should count as activity. Centralizing here means "visible StatusText actually changed" is
    // the one true activity signal, regardless of which code path changed it - SetProperty's own
    // equality check (see ViewModelBase) already means this only fires when the text truly
    // differs from what's currently shown, so reporting the same string again is correctly not
    // activity.
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(HeaderSubtitleText));
                StampLastActivity();
            }
        }
    }

    public string LastActivityText
    {
        get => _lastActivityText;
        private set => SetProperty(ref _lastActivityText, value);
    }

    public bool PreventSleepEnabled
    {
        get => _preventSleepEnabled;
        set => SetProperty(ref _preventSleepEnabled, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Rows.Clear();
        _lastRenderedRowSnapshot = null;

        // The very first WUA pass's own reset happens via the exact same onNewPassStarting
        // callback every later pass boundary uses too (see BeginNewPass/RunOnDedicatedStaWorkerAsync) -
        // no separate reset here, so there is only ever one place that decides "this is a fresh
        // pass baseline".
        try
        {
            State = WindowsUpdatePageState.CheckingConnectivity;
            StatusText = "Checking connectivity...";

            if (!ConnectivityService.HasInternet())
            {
                var config = await _configService.LoadAsync();
                StatusText = "Connecting Wi-Fi...";
                var wifiOutcome = await Task.Run(() => _wifiService.TryConnect(config.Connection, _logger));
                _logger.Info($"Wi-Fi connection attempt result: {wifiOutcome}.");

                if (!ConnectivityService.HasInternet())
                {
                    if (!ConnectivityService.HasWifiAdapter())
                    {
                        State = WindowsUpdatePageState.WaitingForConnection;
                        StatusText = "No internet connection detected. Please connect an Ethernet cable to continue.";
                        await WaitForConnectionAsync();
                    }
                    else
                    {
                        _logger.Warn("Wi-Fi adapter present but not connected, and no internet available.");
                        State = WindowsUpdatePageState.NoConnection;
                        StatusText = "No internet connection available. Check the network and try again.";
                        return;
                    }
                }
            }

            State = WindowsUpdatePageState.Running;
            StatusText = "Connected. Preparing Windows Update...";

            var table = new PendingUpdatesTable(entries => _rowsProgress.Report(entries));

            // No broad flag toggled around the whole run here - WindowsUpdateOrchestrator itself
            // acquires SharedViewState.EnterWuaOperation() leases only around its individual real
            // WUA COM calls (search/download/install), so IsWuaOperationActive correctly drops to
            // false at safe boundaries within the run (e.g. the no-progress backoff sleep) rather
            // than staying true for the run's entire duration.
            var outcome = await RunOnDedicatedStaWorkerAsync(table, _statusProgress, _logger);

            // Session/scheduled-task/reboot lifecycle side effects (Phase 2B) - deliberately run
            // before the display State transition below so a successful RebootRequired handoff
            // (persist -> verify task -> reboot) has already happened by the time the UI shows
            // "restart required". A missing onOutcome delegate (none passed) is a no-op, matching
            // Phase 2A's original detect-only behavior.
            if (_onOutcome is not null)
            {
                await _onOutcome(outcome);
            }

            State = outcome switch
            {
                WindowsUpdateOutcome.FullyUpToDate => WindowsUpdatePageState.FullyUpToDate,
                WindowsUpdateOutcome.RebootRequired => WindowsUpdatePageState.RebootRequired,
                _ => WindowsUpdatePageState.StillPending,
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Windows Update check failed unexpectedly: {ex}");
            State = WindowsUpdatePageState.Failed;
            StatusText = $"Windows Update check failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Invoked by RebootNowCommand (only enabled while IsRebootDeferred) - routes back through the
    // lifecycle coordinator rather than doing anything here directly, so the same
    // persist/verify-task/reboot invariants apply as the original automatic transition. No new
    // WU run is started, no session is reset, and no Setup Stage advance happens - this is purely
    // "the technician explicitly asked to proceed with the reboot that was already prepared".
    private async Task RebootNowAsync()
    {
        if (_onRebootNowRequested is not null)
        {
            await _onRebootNowRequested();
        }
    }

    private async Task WaitForConnectionAsync()
    {
        while (!ConnectivityService.HasInternet())
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // Every update is two equal work units - download, then install - so a single update sitting
    // at "Downloading 50%" has only completed a quarter of its own total work, not half. Contract
    // (0.0-2.0 per update):
    //   Pending             -> 0.0
    //   Downloading p%      -> p/100
    //   Downloaded          -> 1.0
    //   Installing p%       -> 1.0 + p/100
    //   Installed/PendingRestart -> 2.0
    //   Failed              -> 2.0 (terminal - no further work will happen for this item this pass;
    //                          without this, one failed update would hold the aggregate below 100%
    //                          forever even though nothing more is going to happen to it)
    // The Downloaded(1.0) -> Installing 0%(1.0 + 0) transition is continuous by construction - no
    // special-casing needed, the formula itself never steps backwards there.
    private static double ContributionFor(UpdateStatusEntry entry) => entry.State switch
    {
        UpdateState.Pending => 0.0,
        UpdateState.Downloading => Math.Clamp((entry.PercentComplete ?? 0) / 100.0, 0.0, 1.0),
        UpdateState.Downloaded => 1.0,
        UpdateState.Installing => 1.0 + Math.Clamp((entry.PercentComplete ?? 0) / 100.0, 0.0, 1.0),
        UpdateState.Installed => 2.0,
        UpdateState.PendingRestart => 2.0,
        UpdateState.Failed => 2.0,
        _ => 0.0,
    };

    // Builds the one real OverallWindowsUpdateProgressPercent both the full page and the
    // persistent footer bind to. entries is NOT necessarily the whole pass's title universe - the
    // engine deliberately reports narrower subsets at different points within the SAME pass (the
    // executor's download-phase callback carries every title still being downloaded, its
    // install-phase callback carries only the smaller toInstall subset that made it that far, and
    // the availability watcher's MergeNewTitles-driven snapshot can be additive-only too) - see
    // PendingUpdatesTable's own remarks. So this method only ever ADDS/UPDATES entries from what
    // it's given; it never removes a title from _updateContributions just because the latest
    // snapshot doesn't mention it, and the denominator is the dictionary's own accumulated key
    // count for this pass, never entries.Count. A title present in this snapshot gets its
    // contribution recomputed (retaining the highest value seen, so a transient/out-of-order
    // snapshot can never walk the bar backwards); a title this pass already knows about but absent
    // from this particular snapshot simply keeps whatever it last had. The dictionary itself is
    // only ever cleared at a genuine new-pass boundary - see BeginNewPass.
    private void RecomputeOverallProgress(IReadOnlyList<UpdateStatusEntry> entries)
    {
        foreach (var entry in entries)
        {
            var raw = ContributionFor(entry);
            var retained = _updateContributions.TryGetValue(entry.Name, out var previous) ? Math.Max(previous, raw) : raw;
            _updateContributions[entry.Name] = retained;
        }

        if (_updateContributions.Count == 0)
        {
            OverallWindowsUpdateProgressPercent = 0;
            return;
        }

        var sum = _updateContributions.Values.Sum();
        OverallWindowsUpdateProgressPercent = sum / (2.0 * _updateContributions.Count) * 100.0;
    }

    // Fired by WindowsUpdateOrchestrator's onNewPassStarting callback (see
    // RunOnDedicatedStaWorkerAsync) at exactly the two points that genuinely begin a new WUA pass -
    // the very first search, and each subsequent pass-boundary re-search once a DownloadAndInstall
    // cycle finishes and another pass is about to start. Deliberately NOT fired when a pass's own
    // download phase transitions into its install phase (see RecomputeOverallProgress) - that's
    // still the same pass, just a narrower callback shape. A brand new pass must not carry over any
    // title's retained contribution from whatever the previous pass last saw - a title Installed
    // (2.0) in pass 1 that legitimately needs a fresh Pending pass in pass 2 must start that pass
    // at 0.0, not silently keep crediting work a new pass hasn't actually done yet.
    private void BeginNewPass()
    {
        _updateContributions.Clear();
        OverallWindowsUpdateProgressPercent = 0;
        _downloadPhaseEnteredAt.Clear();
        _titlesWithMeasurableDownloadProgress.Clear();
        _lastRenderedRowSnapshot = null;
    }

    // Phase 5, external review: wraps the existing rows-rebuild pipeline (Rows.Clear()/re-add,
    // RecomputeOverallProgress, TrackDownloadPhaseDiagnostics - unchanged) behind an equality
    // gate. A poll occurring is not activity - if the incoming snapshot is visibly identical to
    // what's already rendered, none of that work runs, no CollectionChanged events fire, and
    // LastActivityText is not restamped. Without this, the new 2-second same-STA GetProgress poll
    // (see WindowsUpdateExecutor.WaitForDownloadCompletionWithPolling) would churn the bound Rows
    // collection every 2 seconds even during a genuinely quiet stretch.
    private void HandleRowsProgress(IReadOnlyList<UpdateStatusEntry> entries)
    {
        if (RowSnapshotsEqual(_lastRenderedRowSnapshot, entries))
        {
            return;
        }

        _lastRenderedRowSnapshot = entries.Select(e => (e.Name, e.State, e.PercentComplete)).ToList();

        Rows.Clear();
        foreach (var entry in entries)
        {
            Rows.Add(new UpdateRowViewModel(entry));
        }

        RecomputeOverallProgress(entries);
        TrackDownloadPhaseDiagnostics(entries);
        StampLastActivity();
    }

    // StatusText's own setter now stamps Last Activity itself whenever the text actually changes
    // (see StatusText's own remarks) - this is just the Progress<string> forwarding target.
    private void HandleStatusProgress(string text) => StatusText = text;

    private void StampLastActivity() => LastActivityText = $"Last activity: {DateTime.Now:HH:mm:ss}";

    private static bool RowSnapshotsEqual(
        List<(string Name, UpdateState State, int? Percent)>? previous,
        IReadOnlyList<UpdateStatusEntry> current)
    {
        if (previous is null || previous.Count != current.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (previous[i].Name != current[i].Name ||
                previous[i].State != current[i].State ||
                previous[i].Percent != current[i].PercentComplete)
            {
                return false;
            }
        }

        return true;
    }

    // Diagnostics only - see _downloadPhaseEnteredAt/_titlesWithMeasurableDownloadProgress's own
    // remarks. Writes exactly two Verbose.log lines per title per pass: once when it's first seen
    // Downloading (any percent), and once when it first reports real (>0) measurable progress,
    // with the elapsed time between them - purely observational, never changes State/PercentText,
    // never declares anything "stalled".
    private void TrackDownloadPhaseDiagnostics(IReadOnlyList<UpdateStatusEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.State != UpdateState.Downloading)
            {
                continue;
            }

            if (!_downloadPhaseEnteredAt.ContainsKey(entry.Name))
            {
                _downloadPhaseEnteredAt[entry.Name] = DateTime.Now;
                _logger.Info($"[WU] '{entry.Name}' entered download phase.");
            }

            if (entry.PercentComplete is > 0 && _titlesWithMeasurableDownloadProgress.Add(entry.Name))
            {
                var elapsed = _downloadPhaseEnteredAt.TryGetValue(entry.Name, out var enteredAt)
                    ? DateTime.Now - enteredAt
                    : (TimeSpan?)null;
                _logger.Info(elapsed is { } e
                    ? $"[WU] '{entry.Name}' first measurable download progress ({entry.PercentComplete}%) after {e.TotalSeconds:F1}s preparing."
                    : $"[WU] '{entry.Name}' first measurable download progress ({entry.PercentComplete}%).");
            }
        }
    }

    // Bridges the dedicated STA worker (mandatory for WUA COM + SleepPreventionService thread
    // affinity - see WindowsUpdateOrchestrator's remarks) to this async ViewModel method via a
    // TaskCompletionSource, rather than Task.Run/thread-pool continuations. Awaiting this Task
    // resumes back on the UI thread's captured SynchronizationContext, same as everywhere else
    // in this codebase (InstallPageViewModel's Progress<string> pattern).
    private Task<WindowsUpdateOutcome> RunOnDedicatedStaWorkerAsync(PendingUpdatesTable table, IProgress<string> statusProgress, Logger logger)
    {
        var tcs = new TaskCompletionSource<WindowsUpdateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new Thread(() =>
        {
            try
            {
                var orchestrator = new WindowsUpdateOrchestrator(_executor);

                // Called directly on this STA worker thread by the orchestrator, at exactly the
                // two points that genuinely begin a new WUA pass - marshaled to the UI thread the
                // same way ToolkitOperationActiveChanged already is in this class's constructor,
                // since BeginNewPass mutates bound state and must run there.
                var result = orchestrator.Run(
                    table,
                    text => statusProgress.Report(text),
                    () => PreventSleepEnabled,
                    logger,
                    _viewState.EnterWuaOperation,
                    onNewPassStarting: () => Application.Current?.Dispatcher.BeginInvoke(BeginNewPass));
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        // Matches Setup Agent's own worker - STA is the safer default assumption for COM
        // interop generally, and this is where the WUA COM objects get created/used.
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        return tcs.Task;
    }
}
