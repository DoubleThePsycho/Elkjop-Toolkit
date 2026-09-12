using System.Collections.ObjectModel;
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
        StateText = DescribeState(entry.State);
        PercentText = DescribePercent(entry.PercentComplete);
    }

    public string Name { get; }
    public string StateText { get; }
    public string PercentText { get; }

    private static string DescribeState(UpdateState state) => state switch
    {
        UpdateState.Downloading => "Downloading",
        UpdateState.Downloaded => "Downloaded",
        UpdateState.Installing => "Installing",
        UpdateState.Installed => "Installed",
        UpdateState.Failed => "Failed",
        UpdateState.PendingRestart => "Pending restart",
        _ => "Pending",
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
    private string _lastCheckedText = "";
    private bool _preventSleepEnabled = true;
    private bool _isBusy;
    private bool _isRebootDeferred;

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

        _rowsProgress = new Progress<IReadOnlyList<UpdateStatusEntry>>(entries =>
        {
            Rows.Clear();
            foreach (var entry in entries)
            {
                Rows.Add(new UpdateRowViewModel(entry));
            }
        });

        _statusProgress = new Progress<string>(text =>
        {
            StatusText = text;
            LastCheckedText = $"Last checked: {DateTime.Now:HH:mm:ss}";
        });

        // Re-raised whenever the row snapshot changes - the only thing ProgressSummaryText reads.
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ProgressSummaryText));

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        RebootNowCommand = new AsyncRelayCommand(RebootNowAsync, () => IsRebootDeferred);
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

    // Compact status line for the footer - "needs attention" for the states where StatusText
    // alone (e.g. a stale "Windows Update check failed: ...") would be too verbose for a small
    // strip, "Restart required to continue" for a deferred reboot, StatusText otherwise (mirrors
    // exactly what the full Windows Update page itself shows while running).
    public string FooterStatusText => IsRebootDeferred
        ? "Restart required to continue"
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

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(HeaderSubtitleText));
            }
        }
    }

    public string LastCheckedText
    {
        get => _lastCheckedText;
        private set => SetProperty(ref _lastCheckedText, value);
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
                var result = orchestrator.Run(table, text => statusProgress.Report(text), () => PreventSleepEnabled, logger, _viewState.EnterWuaOperation);
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
