using Support_Tool.Models;

namespace Support_Tool.ViewModels;

public class SharedViewState : ViewModelBase
{
    private bool _isGridView;
    private int _activeWuaOperationCount;
#if DEBUG
    private bool _isDryRun = true;
#else
    private readonly bool _isDryRun = false;
#endif

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
                OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !_isGridView;

    // Thread-safe reference count of live WUA COM operations (search/download/install), not a
    // plain writable bool: the dedicated STA worker thread (WindowsUpdatePageViewModel /
    // WindowsUpdateOrchestrator.Run) and the availability watcher's own background thread
    // (WindowsUpdateOrchestrator.StartAvailabilityWatcher) can independently be inside a WUA call
    // at the same time, so a single bool toggled true/false around individual calls would race
    // and could report "not active" while the other thread's call is still live. True only while
    // at least one WUA COM operation is actually in flight - not for the whole time the Windows
    // Update page happens to be open, and specifically NOT during safe boundaries like the
    // orchestrator's no-progress backoff sleep. MainWindow's OnClosing reads this to refuse a
    // close that would tear down the process underneath a live COM operation.
    // Phase 2A only: no session.json, no Runtime teardown, no ResumeRequired handling hangs off
    // this yet - it exists purely to make closing safe now that real WUA calls can execute.
    // No UI binding depends on this value today, so it's a plain computed property - no
    // SetProperty/OnPropertyChanged/Dispatcher machinery needed; Interlocked/Volatile are safe to
    // touch from any thread without marshaling.
    public bool IsWuaOperationActive => Volatile.Read(ref _activeWuaOperationCount) > 0;

    // Acquire for exactly the duration of one real WUA COM call (see WindowsUpdateOrchestrator's
    // InWuaScope helper) and Dispose() it immediately after - never held across Thread.Sleep,
    // status/UI updates, waiting for a connection, or other non-WUA control flow.
    public IDisposable EnterWuaOperation()
    {
        Interlocked.Increment(ref _activeWuaOperationCount);
        return new WuaOperationLease(this);
    }

    private void ExitWuaOperation() => Interlocked.Decrement(ref _activeWuaOperationCount);

    private sealed class WuaOperationLease(SharedViewState owner) : IDisposable
    {
        private SharedViewState? _owner = owner;

        public void Dispose()
        {
            // Interlocked.Exchange makes a double-Dispose harmless (decrements at most once)
            // rather than letting the count drift permanently positive.
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ExitWuaOperation();
        }
    }

    public bool IsDryRun
    {
        get => _isDryRun;
#if DEBUG
        set
        {
            if (SetProperty(ref _isDryRun, value))
                OnPropertyChanged(nameof(IsLiveMode));
        }
#endif
    }

    public bool IsLiveMode => !_isDryRun;

#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    public ExecutionMode ExecutionMode => _isDryRun ? ExecutionMode.DryRun : ExecutionMode.Real;
}
