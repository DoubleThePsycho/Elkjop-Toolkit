using Support_Tool.Services;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool.ViewModels;

// Portable-config editor: Account.Username and Connection.WifiName/WifiPassword are the only
// editable fields in this pass (Debloater/Installer stay untouched, out of scope here). Saving
// always updates the local staged Runtime\config.json first (that's what this running Support
// Tool actually reads), then makes a best-effort attempt to also update the canonical USB copy
// by relocating the originating volume via RuntimeStaging.SourcePath/PortableConfigSourceLocator
// (volume serial number, not drive letter). The technician is told plainly which of those two
// things actually happened, per ConfigService.SaveAsync's own result.
//
// This ViewModel is a long-lived singleton for the whole process (constructed once in
// App.xaml.cs), so it must not rely on constructor-time state forever - HasLoaded/LoadAsync
// follow the same lazy-load-on-first-navigation shape InstallPageViewModel/
// InstalledItemsPageViewModel already use (see MainViewModel.NavigateTo), while
// RefreshPortableStatusAsync is cheap/read-only and is called on every navigation to this page
// regardless of HasLoaded - the USB can be inserted, removed, or reinserted under a different
// drive letter at any point while Support Tool keeps running, so the status shown must reflect
// the current state, not just whatever existed the first time this page was opened.
public class SettingsPageViewModel : ViewModelBase
{
    private readonly ConfigService _configService;

    // The full document as last loaded/saved - Debloater/Installer are carried from here
    // untouched into every save, so editing account/Wi-Fi fields can never erase or recreate
    // them. Null exactly when HasLoaded is false - the two are kept in lockstep so nothing can
    // ever save with a null/default baseline while HasLoaded claims success.
    private UnifiedToolkitConfig? _loadedConfig;

    // Reentrancy guard - a second navigation (or a Save click) while a load is still in flight
    // awaits that same in-progress attempt instead of starting a redundant/overlapping one.
    private Task? _loadTask;

    private string _username = string.Empty;
    private string _wifiName = string.Empty;
    private string _wifiPassword = string.Empty;
    private string _portableStatusText = "Checking portable configuration source...";
    private string? _saveResultText;

    public SharedViewState ViewState { get; }
    public AsyncRelayCommand SaveCommand { get; }

    // Means "successfully loaded", not merely "a load was attempted" - a failed load must leave
    // this false so a later navigation (or Save) retries rather than treating empty/default
    // editor fields as a legitimate loaded state. See LoadAsync's own remarks.
    public bool HasLoaded { get; private set; }

    public SettingsPageViewModel(SharedViewState viewState, ConfigService configService)
    {
        ViewState = viewState;
        _configService = configService;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string WifiName
    {
        get => _wifiName;
        set => SetProperty(ref _wifiName, value);
    }

    // No dedicated PasswordBox binding support in WPF (IsChecked-style binding would expose the
    // password in the visual tree) - SettingsPage.xaml.cs's PasswordChanged handler sets this
    // directly from the PasswordBox's own Password property instead.
    public string WifiPassword
    {
        get => _wifiPassword;
        set => SetProperty(ref _wifiPassword, value);
    }

    public string PortableStatusText
    {
        get => _portableStatusText;
        private set => SetProperty(ref _portableStatusText, value);
    }

    public string? SaveResultText
    {
        get => _saveResultText;
        private set => SetProperty(ref _saveResultText, value);
    }

    // Loads the current Runtime config into the editable fields - once per process on success,
    // like InstallPageViewModel's own HasLoaded/LoadAsync pattern, but unlike that pattern a
    // FAILED load must not permanently latch HasLoaded=true: MainViewModel.NavigateTo (and
    // SaveAsync below) rely on HasLoaded meaning "safe to build a save from" - if a failed load
    // left it true with _loadedConfig still null/default, a later Save could combine an
    // unrelated, incidentally-successful baseline load with still-empty editor fields and
    // silently blank out Account/Connection. A concurrent second call (another navigation, or
    // Save's own retry) awaits the same in-flight attempt rather than starting a redundant one.
    //
    // _loadTask's lifetime is owned entirely by this wrapper, not by LoadCoreAsync itself -
    // clearing it from inside LoadCoreAsync's own finally would race when LoadCoreAsync completes
    // synchronously (e.g. an immediate failure): the `??=` below wouldn't assign _loadTask until
    // AFTER LoadCoreAsync has already returned, so its finally would have already reset a field
    // that hadn't been set yet, and the subsequent `??=` would then wrongly latch a stale
    // (already-completed, possibly-failed) task into _loadTask - a later LoadAsync() call would
    // see _loadTask non-null and just re-await that dead task forever instead of starting a
    // genuinely new attempt.
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
            // Only clear if it's still the exact task this call started/observed - a concurrent
            // caller that already moved on to a newer attempt must not have its own in-flight
            // task field wiped out from under it.
            if (ReferenceEquals(_loadTask, task))
            {
                _loadTask = null;
            }
        }
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            Username = config.Account.Username;
            WifiName = config.Connection.WifiName;
            WifiPassword = config.Connection.WifiPassword;
            _loadedConfig = config;
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            _loadedConfig = null;
            HasLoaded = false;
            SaveResultText = $"Could not load the current configuration: {ex.Message}";
        }
    }

    // Read-only status check - never writes anything, so it's always safe to call, including on
    // every navigation to this page regardless of HasLoaded.
    public Task RefreshPortableStatusAsync()
    {
        var sourceInfo = PortableConfigSourceLocator.Load(RuntimeStaging.SourcePath);
        if (sourceInfo is null)
        {
            PortableStatusText = "No portable USB source recorded for this session - saves will be local only.";
            return Task.CompletedTask;
        }

        var resolvedPath = PortableConfigSourceLocator.Resolve(sourceInfo);
        PortableStatusText = resolvedPath is not null
            ? $"Portable source connected: \"{resolvedPath}\"."
            : "Portable source recorded, but that USB is not currently connected - saves will be local only until it's reconnected.";

        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        // Never write Account/Connection unless the editor fields were actually initialized from
        // a successfully loaded Runtime config during this process - a Save clicked before the
        // first load ever succeeded (or after one failed) retries the load here, but if it's
        // still not successfully loaded afterward, this stops without writing anything rather
        // than risk combining a stale/absent baseline with empty editor fields.
        if (!HasLoaded)
        {
            await LoadAsync();
        }

        if (!HasLoaded || _loadedConfig is null)
        {
            SaveResultText = "Could not save: the current configuration has not been loaded successfully. Reopen Settings and try again.";
            return;
        }

        try
        {
            // AccountConfig/ConnectionConfig stay the init-only immutable DTOs they already are -
            // a complete replacement root is built fresh from the edited fields plus whatever
            // Debloater/Installer content was last loaded/saved, never a partial document and
            // never a mutation of shared state.
            var baseline = _loadedConfig;
            var updated = new UnifiedToolkitConfig
            {
                Account = new AccountConfig { Username = Username },
                Connection = new ConnectionConfig { WifiName = WifiName, WifiPassword = WifiPassword },
                Debloater = baseline.Debloater,
                Installer = baseline.Installer,
            };

            var result = await _configService.SaveAsync(updated);

            // The local Runtime write inside SaveAsync either succeeds or throws (caught below) -
            // only the USB half is ever best-effort/failable (reported via result.Outcome), so
            // reaching this line means the local save genuinely went through and it's safe to
            // adopt `updated` as the new baseline for the next save.
            _loadedConfig = updated;

            SaveResultText = result.Outcome switch
            {
                ConfigPortableSaveOutcome.PortableUpdated =>
                    $"Saved. Also updated the canonical USB configuration at \"{result.PortablePath}\".",
                ConfigPortableSaveOutcome.NoSourceRecorded =>
                    "Saved locally. This session has no recorded portable USB source, so the change is not portable.",
                ConfigPortableSaveOutcome.SourceNotConnected =>
                    "Saved locally. The originating USB is not currently connected - reconnect it and save again to make this change portable.",
                ConfigPortableSaveOutcome.PortableWriteFailed =>
                    $"Saved locally, but could not update the USB copy: {result.ErrorMessage}",
                _ => "Saved.",
            };

            await RefreshPortableStatusAsync();
        }
        catch (Exception ex)
        {
            SaveResultText = $"Could not save: {ex.Message}";
        }
    }
}
