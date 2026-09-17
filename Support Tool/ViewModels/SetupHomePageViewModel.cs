using System.Collections.ObjectModel;
using Support_Tool.Services;
using Toolkit.Shared.Configuration;

namespace Support_Tool.ViewModels;

// Plain display option for the browser selector's ComboBox - deliberately not a duplicate
// InstallableApp definition, just Name+WingetId sourced from the existing "Browsers" catalog
// category (see SetupHomePageViewModel.LoadAsync).
public sealed record BrowserOption(string Name, string WingetId)
{
    public override string ToString() => Name;
}

// Setup Home - the Setup workflow's own Home page (see App.xaml.cs's ResolveStartupNavigation and
// MainViewModel.NavigateHomeCommand, both of which route a Setup session here instead of the
// generic HomePageViewModel Service uses). The Service workflow never constructs or navigates to
// this page - its own routing/HomePageViewModel/ServicePageViewModel are completely unchanged.
//
// SA Standard/SA Gaming are presets over the EXISTING Install catalog (config-driven via
// Installer.SetupPresets - see ConfigService.CreateDefaultSetupPresets), never a second install
// engine: applying one only sets InstallPageViewModel's pending selection and navigates there -
// the technician still reviews, and can still change, exactly what will be installed via
// InstallPageViewModel's own existing "Install Selected" command.
public class SetupHomePageViewModel : ViewModelBase
{
    private readonly ConfigService _configService;
    private readonly InstallCatalogService _catalog;
    private readonly InstallPageViewModel _installPage;
    private readonly Logger _logger = new();

    private BrowserOption? _selectedBrowser;
    private string _standardDescription = "";
    private string _gamingDescription = "";

    // Cached from LoadAsync so ApplyPresetAsync (and the description text below) don't each read
    // config.json independently - SetupPresets is not something the technician edits mid-session
    // (Settings has no UI for it - see SetupPresetConfig's own remarks), so a one-time read per
    // page load is correct, not a staleness risk.
    private Dictionary<string, SetupPresetConfig> _presets = new();

    // Category name -> every app's WingetId currently in that category, cached from the same
    // catalog structure LoadAsync already fetched - resolved from InstallCatalogService's fast,
    // no-per-app-I/O catalog structure (not InstallPageViewModel's own, possibly-still-loading,
    // per-app status), so a preset's CategoryNames can be expanded immediately even while the
    // Install page itself is still incrementally checking install status (see
    // SetupPresetConfig.CategoryNames's own remarks).
    private Dictionary<string, List<string>> _wingetIdsByCategory = new(StringComparer.OrdinalIgnoreCase);

    public SetupHomePageViewModel(ConfigService configService, InstallCatalogService catalog, InstallPageViewModel installPage)
    {
        _configService = configService;
        _catalog = catalog;
        _installPage = installPage;

        ApplyStandardCommand = new AsyncRelayCommand(() => ApplyPresetAsync("SA Standard"));
        ApplyGamingCommand = new AsyncRelayCommand(() => ApplyPresetAsync("SA Gaming"));
    }

    // Wired post-construction by App.xaml.cs once MainViewModel (which owns the real navigation
    // commands) exists - same ordering idiom already used for
    // WindowsUpdateLifecycleCoordinator.Page. Always routes to the raw Install catalog directly,
    // deliberately bypassing MainViewModel.NavigateInstallCommand's own Setup-aware redirect back
    // to this page - applying a preset must land the technician ON Install, not loop them back
    // here.
    public Action? NavigateToInstall { get; set; }

    public bool HasLoaded { get; private set; }

    public ObservableCollection<BrowserOption> BrowserOptions { get; } = new();

    public BrowserOption? SelectedBrowser
    {
        get => _selectedBrowser;
        set => SetProperty(ref _selectedBrowser, value);
    }

    public AsyncRelayCommand ApplyStandardCommand { get; }
    public AsyncRelayCommand ApplyGamingCommand { get; }

    // Human-readable "what's in this package" text for each card - built from the actual loaded
    // catalog/preset config rather than hardcoded, so editing Installer.SetupPresets in config.json
    // (e.g. adding real gaming titles later, per the store's own "more added later" note) is
    // reflected here with no code change.
    public string StandardDescription
    {
        get => _standardDescription;
        private set => SetProperty(ref _standardDescription, value);
    }

    public string GamingDescription
    {
        get => _gamingDescription;
        private set => SetProperty(ref _gamingDescription, value);
    }

    public async Task LoadAsync()
    {
        if (HasLoaded)
        {
            return;
        }

        HasLoaded = true;

        try
        {
            var categories = await _catalog.GetCatalogAsync();
            var browsers = categories.FirstOrDefault(c => c.Name.Equals("Browsers", StringComparison.OrdinalIgnoreCase));

            if (browsers is not null)
            {
                foreach (var app in browsers.Apps)
                {
                    BrowserOptions.Add(new BrowserOption(app.Name, app.WingetId));
                }
            }

            // Firefox default, per the store's stated preference - falls back to whatever the
            // catalog's own first Browsers entry is if Firefox isn't present, rather than leaving
            // nothing selected.
            SelectedBrowser =
                BrowserOptions.FirstOrDefault(b => b.WingetId.Equals("Mozilla.Firefox", StringComparison.OrdinalIgnoreCase))
                ?? BrowserOptions.FirstOrDefault();

            var config = await _configService.LoadAsync();
            _presets = config.Installer.SetupPresets;

            _wingetIdsByCategory = categories.ToDictionary(
                c => c.Name,
                c => c.Apps.Select(a => a.WingetId).ToList(),
                StringComparer.OrdinalIgnoreCase);

            // One WingetId can legitimately map to MORE than one catalog row (Java 8's x86/x64
            // rows share one WingetId) - every distinct display name for that ID must show up in
            // the description, not just the first one found, or the card would silently claim to
            // install only one of the two architectures.
            var namesByWingetId = categories
                .SelectMany(c => c.Apps)
                .GroupBy(a => a.WingetId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            StandardDescription = DescribePreset("SA Standard", namesByWingetId);
            GamingDescription = DescribePreset("SA Gaming", namesByWingetId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Setup Home failed to load: {ex.Message}");
        }
    }

    private string DescribePreset(string presetName, IReadOnlyDictionary<string, List<string>> namesByWingetId)
    {
        if (!_presets.TryGetValue(presetName, out var preset)
            || (preset.WingetIds.Count == 0 && preset.CategoryNames.Count == 0))
        {
            return "No applications configured.";
        }

        // Expand each WingetId to EVERY catalog row it matches (not just one) - this is what
        // makes both "Java 8 (32-bit)" and "Java 8 (64-bit)" show up for a preset that lists
        // Amazon.Corretto.8.JRE once.
        var names = preset.WingetIds
            .SelectMany(id => namesByWingetId.TryGetValue(id, out var matchNames) ? matchNames : [id])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Category-driven apps (e.g. "Game Launchers") are named as a category, not enumerated
        // one by one - listing seven launcher names would be an unreadable wall, and the whole
        // point of a category reference is that its membership can change without this text
        // needing to change either.
        var parts = new List<string>(1 + names.Count + preset.CategoryNames.Count) { "Browser" };
        parts.AddRange(names);
        parts.AddRange(preset.CategoryNames);

        return string.Join(", ", parts);
    }

    private async Task ApplyPresetAsync(string presetName)
    {
        var selection = new List<string>();

        if (_presets.TryGetValue(presetName, out var preset))
        {
            selection.AddRange(preset.WingetIds);

            foreach (var categoryName in preset.CategoryNames)
            {
                if (_wingetIdsByCategory.TryGetValue(categoryName, out var categoryWingetIds))
                {
                    selection.AddRange(categoryWingetIds);
                }
                else
                {
                    _logger.Warn($"Setup Home: preset '{presetName}' references category '{categoryName}', which was not found in the Install catalog.");
                }
            }
        }

        if (selection.Count == 0)
        {
            _logger.Warn($"Setup Home: preset '{presetName}' has no configured apps.");
        }

        // The browser is merged in here rather than baked into the preset config itself - the
        // technician's own selector choice always wins, and the preset stays purely about the
        // rest of the package (see SetupPresetConfig's own remarks).
        if (SelectedBrowser is not null)
        {
            selection.Add(SelectedBrowser.WingetId);
        }

        _installPage.ApplyPresetSelection(selection);
        NavigateToInstall?.Invoke();
    }
}
