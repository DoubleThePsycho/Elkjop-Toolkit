using System.Collections.ObjectModel;
using Support_Tool.Services;
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

// The shared Windows Update page ViewModel - one instance, constructed once in App.xaml.cs and
// used by both SetupPageViewModel (embedded directly - Windows Update is the mandatory gate) and
// ServicePageViewModel (navigated to and away from - an optional tool), per the approved plan.
// Re-hosts Setup Agent's orchestration (WindowsUpdateOrchestrator) on a dedicated STA worker
// thread; this ViewModel itself only ever touches WPF-bound state from the UI thread, using
// Progress<T> to receive callbacks that may originate from the STA worker, the availability
// watcher's own background thread, or WUA's own COM callback threads.
//
// Phase 2A detected and displayed RebootRequired but never rebooted or persisted session state.
// Phase 2B adds that lifecycle via the optional onOutcome delegate (constructed in App.xaml.cs as
// WindowsUpdateLifecycleCoordinator.HandleOutcomeAsync) - this ViewModel stays workflow-agnostic
// itself; the coordinator is the one that branches on SessionState.Workflow.
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

    public WindowsUpdatePageViewModel(
        IWindowsUpdateExecutor executor,
        IWifiConnectionService wifiService,
        ConfigService configService,
        SharedViewState viewState,
        Func<WindowsUpdateOutcome, Task>? onOutcome = null)
    {
        _executor = executor;
        _wifiService = wifiService;
        _configService = configService;
        _viewState = viewState;
        _onOutcome = onOutcome;

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

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
    }

    public ObservableCollection<UpdateRowViewModel> Rows { get; } = [];
    public AsyncRelayCommand StartCommand { get; }

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

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
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
