using Support_Tool.Models;

namespace Support_Tool.ViewModels;

public class SharedViewState : ViewModelBase
{
    private bool _isGridView;
    private int _activeWuaOperationCount;
    private int _activeLifecycleTransitionCount;
    private int _activeToolkitOperationCount;
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

    // Separate ref-counted flag from IsWuaOperationActive - deliberately NOT folded into it.
    // IsWuaOperationActive stays scoped exactly to live WUA COM calls (Phase 2A's contract,
    // unchanged); this one instead covers WindowsUpdateLifecycleCoordinator's own async
    // session/task/reboot side-effect transaction (persist session -> create/verify the resume
    // task -> reboot, or persist terminal state -> delete the resume task -> navigate) - a window
    // where no WUA COM call is in flight (so IsWuaOperationActive is already false) but closing
    // the process mid-transaction could still strand a half-applied transition: a reboot that
    // fires with the task not yet verified, or a navigation that happens before the resume task
    // is actually confirmed deleted. MainWindow's OnClosing checks both flags independently.
    public bool IsLifecycleTransitionActive => Volatile.Read(ref _activeLifecycleTransitionCount) > 0;

    public IDisposable EnterLifecycleTransition()
    {
        Interlocked.Increment(ref _activeLifecycleTransitionCount);
        return new LifecycleTransitionLease(this);
    }

    private void ExitLifecycleTransition() => Interlocked.Decrement(ref _activeLifecycleTransitionCount);

    private sealed class LifecycleTransitionLease(SharedViewState owner) : IDisposable
    {
        private SharedViewState? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ExitLifecycleTransition();
        }
    }

    private volatile bool _lifecycleRecoveryBlocked;

    // Distinct from both IsWuaOperationActive (live COM calls) and IsLifecycleTransitionActive
    // (a normal, expected-to-complete transaction): set only in the exceptional case where a
    // terminal Windows Update completion's resume-task deletion failed AND the subsequent
    // rollback of the persisted Stage/ResumeRequired also failed. In that state session.json may
    // disagree with the actual scheduled-task state in a way Tier 2's plain ResumeRequired check
    // can't detect (a failed rollback can leave ResumeRequired=false on disk even though the task
    // might still be registered), so this is checked unconditionally, ahead of every other close
    // tier, and never clears itself automatically - the app must not exit until someone
    // investigates. Not ref-counted like the other two flags: this represents one terminal
    // "stuck" condition, not a scope that's expected to end normally.
    public bool IsLifecycleRecoveryBlocked
    {
        get => _lifecycleRecoveryBlocked;
        set => _lifecycleRecoveryBlocked = value;
    }

    // A generic, non-WUA "another machine-changing Support Tool operation is in flight" lease -
    // app install/uninstall and classic-program-uninstall/debloat operations acquire this around
    // their real execution lifetime (not around button clicks, navigation, or inventory/loading
    // work). Deliberately separate from IsWuaOperationActive: that flag stays scoped exactly to
    // live WUA COM calls (unchanged), and this one has nothing to do with Windows Update itself -
    // it exists so WindowsUpdateLifecycleCoordinator can refuse to reboot out from under an
    // in-progress install/uninstall/debloat action, which WUA's own state has no way to know
    // about. Same thread-safe ref-counted lease pattern as EnterWuaOperation/EnterLifecycleTransition
    // - more than one such operation could in principle overlap (e.g. an install queued while an
    // uninstall is finishing), so a plain bool would risk one operation's completion incorrectly
    // clearing the flag while another is still running.
    public event Action? ToolkitOperationActiveChanged;

    public bool IsToolkitOperationActive => Volatile.Read(ref _activeToolkitOperationCount) > 0;

    public IDisposable EnterToolkitOperation()
    {
        if (Interlocked.Increment(ref _activeToolkitOperationCount) == 1)
        {
            ToolkitOperationActiveChanged?.Invoke();
        }

        return new ToolkitOperationLease(this);
    }

    private void ExitToolkitOperation()
    {
        if (Interlocked.Decrement(ref _activeToolkitOperationCount) == 0)
        {
            ToolkitOperationActiveChanged?.Invoke();
        }
    }

    private sealed class ToolkitOperationLease(SharedViewState owner) : IDisposable
    {
        private SharedViewState? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ExitToolkitOperation();
        }
    }

    // The setter must exist (compile) in every configuration - SettingsPage.xaml's CheckBox binds
    // IsChecked with Mode=TwoWay unconditionally (its containing section is merely
    // Visibility-collapsed in RELEASE via IsDebugBuild, not removed from the visual tree), and a
    // TwoWay binding against a property with no public setter at all is what caused the real
    // RELEASE Settings-page crash this fixes. RELEASE must still never be genuinely switchable
    // into Dry Run, so its setter body intentionally does nothing - _isDryRun stays the readonly
    // `false` it was constructed with (see the field declaration above); only DEBUG's mutable
    // backing field is ever actually written.
    public bool IsDryRun
    {
        get => _isDryRun;
        set
        {
#if DEBUG
            if (SetProperty(ref _isDryRun, value))
                OnPropertyChanged(nameof(IsLiveMode));
#endif
        }
    }

    public bool IsLiveMode => !_isDryRun;

#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    public ExecutionMode ExecutionMode => _isDryRun ? ExecutionMode.DryRun : ExecutionMode.Real;
}
