namespace Account_Setup.Services;

// Phase 5: the unified root dispatcher (Elkjop Toolkit.exe) releases its own Runtime staging
// lease before launching Account Setup - it cannot hold that mutex across the minutes of
// technician interaction with SetupWindow. That leaves a real window where a second dispatcher
// launch (accidental double-click, or a technician retrying because nothing visibly happened
// yet) sees no live Runtime mutex and no session yet either, and also starts a second Account
// Setup process. This dedicated, Account-Setup-only mutex closes that gap: the loser refuses
// immediately, before touching Runtime, config, or showing any UI.
//
// Mirrors Toolkit.Shared.RuntimeProcessLease's exact ReleaseMutex()-then-Dispose() shape - a
// Mutex requires ReleaseMutex() to be called from the exact thread that acquired it (a hard
// Win32/.NET requirement, not just convention), so a bare `using var Mutex` is not sufficient
// here. Account Setup's Main is one synchronous, top-to-bottom [STAThread] method, so acquiring
// via TryAcquire and disposing the returned lease via a `using var` at the top of Main disposes
// it, on that same thread, on every return path (including both existing catch blocks) with no
// additional cleanup code needed anywhere else - and, critically, a normal clean exit (finish or
// Cancel) always calls ReleaseMutex() first, so an immediate relaunch afterward acquires normally
// rather than going through the AbandonedMutexException path.
internal sealed class AccountSetupInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private AccountSetupInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static AccountSetupInstanceLease? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, AppInfo.SingleInstanceMutexName, out _);
        bool acquired;

        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A prior instance died without releasing - not a live conflict.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new AccountSetupInstanceLease(mutex);
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
