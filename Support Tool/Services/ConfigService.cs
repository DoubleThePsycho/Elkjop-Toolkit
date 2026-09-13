using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

// Thrown when the staged Runtime\config.json is missing or fails to load - deliberately
// distinct from JsonConfigStore's swallow-and-default behavior, which was tolerable back when
// config.json held only non-critical UI preferences. Now that it also carries the Wi-Fi/account
// configuration the Setup workflow's mandatory Windows Update gate depends on, a missing or
// corrupt file must fail visibly rather than silently proceeding on manufactured defaults.
public sealed class ConfigLoadException : Exception
{
    public ConfigLoadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

// What happened to the canonical USB copy when ConfigService.SaveAsync ran - the local
// Runtime\config.json save itself never fails silently (an exception there propagates normally),
// but the USB half of the save is always best-effort, so its outcome is reported back instead of
// thrown.
public enum ConfigPortableSaveOutcome
{
    // The local save succeeded, and the resolved USB copy was also updated - PortablePath names
    // exactly where.
    PortableUpdated,

    // The local save succeeded. This session has no recorded portable source at all (e.g. an
    // older Runtime staged before source.json existed) - the change is local-only.
    NoSourceRecorded,

    // The local save succeeded. A portable source is recorded, but no currently-mounted drive's
    // volume serial number matches it right now - the change is local-only until that USB is
    // reconnected and saved again.
    SourceNotConnected,

    // The local save succeeded, and the originating USB was found, but writing to it failed
    // (ErrorMessage explains why) - the change is local-only for now.
    PortableWriteFailed,
}

public sealed record ConfigSaveResult(ConfigPortableSaveOutcome Outcome, string? PortablePath, string? ErrorMessage);

// Single owner of config.json. RuleService and InstallCatalogService each read
// their own section (Debloater / Installer) from here instead of independently
// hitting the file — two independent JsonConfigStore writers on the same path
// would each serialize only their own DTO and silently wipe the other's section.
// Now that section, spans all four sections of the shared UnifiedToolkitConfig
// (Account/Connection are Account Setup's, read-but-not-owned here) instead of
// just Debloater/Installer - see Toolkit.Shared.Configuration.UnifiedToolkitConfig's
// own remarks for why there can only be one root type.
//
// Phase 2B: Support Tool now reads the staged RuntimeStaging.ConfigPath
// (C:\ProgramData\Elkjop\Toolkit\Runtime\config.json) strictly - missing or corrupt fails
// visibly (ConfigLoadException), never silently regenerates defaults. service.exe/Account Setup
// staging (Phase 3/4) are what actually populate that path in production; until then, a DEBUG
// build falls back to the old per-tool USB path ONLY if the Runtime copy isn't there yet, purely
// so the existing direct-from-USB manual test/dev workflow (see dev-scripts/) keeps working
// without a full staging step. This fallback is compiled out of RELEASE entirely - RELEASE
// always requires the staged Runtime config and fails visibly if it's missing/corrupt.
public class ConfigService
{
    private const string ToolName = "Support Tool";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions SaveJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<UnifiedToolkitConfig> LoadAsync()
    {
        var path = ResolveConfigPath();

        if (!File.Exists(path))
        {
            throw new ConfigLoadException($"Configuration file not found at '{path}'.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            throw new ConfigLoadException($"Failed to read configuration file '{path}': {ex.Message}", ex);
        }

        UnifiedToolkitConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<UnifiedToolkitConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new ConfigLoadException($"Configuration file '{path}' is corrupt: {ex.Message}", ex);
        }

        return config ?? throw new ConfigLoadException($"Configuration file '{path}' deserialized to nothing.");
    }

    /// <summary>
    /// Persists a config change. The local staged Runtime\config.json is always updated first -
    /// that copy is what Support Tool itself actually runs against, and must never depend on the
    /// originating USB still being connected. It then makes a best-effort attempt to also update
    /// the canonical USB copy, using RuntimeStaging.SourcePath (see PortableConfigSourceLocator)
    /// to relocate the originating volume regardless of any drive-letter change since staging -
    /// never by assuming a fixed drive letter, and never by scanning for the first drive that
    /// happens to contain a matching file. Failure to update the USB copy is reported back via
    /// <see cref="ConfigSaveResult"/>, never thrown - the local save has already succeeded by that
    /// point and must not be treated as having failed.
    /// </summary>
    public async Task<ConfigSaveResult> SaveAsync(UnifiedToolkitConfig config)
    {
        var json = JsonSerializer.Serialize(config, SaveJsonOptions);

        await WriteAtomicAsync(RuntimeStaging.ConfigPath, json);

        var sourceInfo = PortableConfigSourceLocator.Load(RuntimeStaging.SourcePath);
        if (sourceInfo is null)
        {
            return new ConfigSaveResult(ConfigPortableSaveOutcome.NoSourceRecorded, null, null);
        }

        var resolvedPath = PortableConfigSourceLocator.Resolve(sourceInfo);
        if (resolvedPath is null)
        {
            return new ConfigSaveResult(ConfigPortableSaveOutcome.SourceNotConnected, null, null);
        }

        try
        {
            await WriteAtomicAsync(resolvedPath, json);
            return new ConfigSaveResult(ConfigPortableSaveOutcome.PortableUpdated, resolvedPath, null);
        }
        catch (Exception ex)
        {
            return new ConfigSaveResult(ConfigPortableSaveOutcome.PortableWriteFailed, null, ex.Message);
        }
    }

    private static async Task WriteAtomicAsync(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ResolveConfigPath()
    {
#if DEBUG
        // Development-only escape hatch - service.exe/Account Setup staging don't populate
        // Runtime\config.json yet (Phase 3/4), so there's currently no shipping way to get a real
        // staged config onto a dev machine except the Phase 2B test-staging script. If the
        // Runtime copy is missing AND the old USB-relative path exists (i.e. Support Tool is
        // being run directly from the USB, as the still-live Support Tool.bat path does), fall
        // back to it so debugging keeps working without staging first. Never present in RELEASE.
        if (!File.Exists(RuntimeStaging.ConfigPath))
        {
            var legacyPath = Path.Combine(ToolkitPaths.GetConfigFolder(ToolName), "config.json");
            if (File.Exists(legacyPath))
            {
                return legacyPath;
            }
        }
#endif
        return RuntimeStaging.ConfigPath;
    }

    // Kept public so the Phase 2B dev-scripts test-staging helper (and, later, Service
    // Launcher/Account Setup staging) can seed a real config.json without duplicating this
    // catalog data - LoadAsync itself no longer calls this on a missing file (see class remarks).
    public static UnifiedToolkitConfig CreateDefaultConfig() => new()
    {
        Debloater = CreateDefaultRuleSet(),
        Installer = CreateDefaultInstallerConfig()
    };

    // Winget IDs confirmed via `winget search` — the "winget" source specifically,
    // not the Microsoft Store listings some of these also have. Icons are resolved
    // at runtime from each app's Link via Google's favicon service (AppIconCacheService).
    private static InstallerConfig CreateDefaultInstallerConfig() => new()
    {
        Categories = new Dictionary<string, InstallCategoryConfig>
        {
            ["Browsers"] = new InstallCategoryConfig
            {
                InstallAll = false,
                Apps =
                [
                    new InstallableApp { Name = "Google Chrome", WingetId = "Google.Chrome", Link = "https://www.google.com/chrome/" },
                    new InstallableApp { Name = "Mozilla Firefox", WingetId = "Mozilla.Firefox", Preselected = true, Link = "https://www.mozilla.org/en-US/firefox/new/" },
                    new InstallableApp { Name = "Brave", WingetId = "Brave.Brave", Link = "https://www.brave.com" },
                    new InstallableApp { Name = "Opera GX", WingetId = "Opera.OperaGX", Link = "https://www.opera.com/gx" },
                ]
            },
            ["Game Launchers"] = new InstallCategoryConfig
            {
                InstallAll = true,
                Apps =
                [
                    new InstallableApp { Name = "Steam", WingetId = "Valve.Steam", Link = "https://store.steampowered.com/about/" },
                    new InstallableApp { Name = "Epic Games Launcher", WingetId = "EpicGames.EpicGamesLauncher", Link = "https://www.epicgames.com/store/en-US/" },
                    // Blizzard.BattleNet's winget manifest requires an explicit install location
                    // (APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED / 0x8A15005F without one) -
                    // this is winget's own documented default location for this package.
                    new InstallableApp { Name = "Battle.net", WingetId = "Blizzard.BattleNet", Link = "https://battle.net/", InstallLocation = "%PROGRAMFILES(X86)%\\Battle.net" },
                    new InstallableApp { Name = "GOG Galaxy", WingetId = "GOG.Galaxy", Link = "https://www.gog.com/galaxy" },
                    new InstallableApp { Name = "Ubisoft Connect", WingetId = "Ubisoft.Connect", Link = "https://ubisoftconnect.com/" },
                    new InstallableApp { Name = "EA app", WingetId = "ElectronicArts.EADesktop", Link = "https://www.ea.com/ea-app" },
                    new InstallableApp { Name = "Rockstar Games Launcher", WingetId = "RockstarGames.Launcher", Link = "https://www.rockstargames.com/launcher" },
                ]
            }
        }
    };

    // Defaults sourced from Elkjop-BasicCustomerCleanup-v5.ps1 (the shop's reference
    // cleanup script) so Support Tool's preselection matches actual shop policy.
    // Publisher-scoped and compound (regex AND) rules in that script can't be
    // represented by a single Match/MatchType pair here and were approximated —
    // see conversation/memory for specifics on what was simplified.
    //
    // Apps entries use MatchType="contains": PackageFullName always carries a
    // version/architecture/publisher-hash suffix, so an "exact" match could never
    // hit it (see RuleService.FindRule).
    private static RuleSet CreateDefaultRuleSet() => new()
    {
        Apps =
        [
            new RuleEntry
            {
                Match = "Microsoft.WindowsStore",
                MatchType = "contains",
                Preselected = false,
                Warn = true,
                Blocked = false,
                Reason = "Usually required for app updates and Store-based dependencies."
            },
            new RuleEntry
            {
                Match = "Microsoft.XboxGamingOverlay",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = ""
            },
            new RuleEntry
            {
                Match = "Microsoft.MicrosoftOfficeHub",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Microsoft 365 promotional shell app is removed for clean customer setup."
            },
            new RuleEntry
            {
                Match = "Microsoft.Office.Desktop",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Microsoft 365/Office Appx bridge is removed together with Microsoft 365."
            },
            new RuleEntry
            {
                Match = "Microsoft.Office.OneNote",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "OneNote is removed per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Microsoft.OneNote",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Catches alternate OneNote Appx naming if present."
            },
            new RuleEntry
            {
                Match = "Microsoft.Whiteboard",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Whiteboard is removed per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Microsoft.RemoteDesktop",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remote Desktop client app is removed per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Microsoft.Copilot",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "AI assistant app is unnecessary for basic customer setup."
            },
            new RuleEntry
            {
                Match = "MicrosoftCorporationII.MicrosoftFamily",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Family Safety app is unnecessary for basic email/web customer setup."
            },
            new RuleEntry
            {
                Match = "Microsoft.OutlookForWindows",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Often preloaded/promoted; customers can still use webmail or install Outlook later."
            },
            new RuleEntry
            {
                Match = "Clipchamp.Clipchamp",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Video editor preload; not needed for basic email/web customers."
            },
            new RuleEntry
            {
                Match = "Microsoft.BingNews",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "News preload; web browser covers this use case."
            },
            new RuleEntry
            {
                Match = "Microsoft.BingSearch",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Search preload; browser and Start search remain available."
            },
            new RuleEntry
            {
                Match = "Microsoft.BingWeather",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Weather preload; not required for laptop setup."
            },
            new RuleEntry
            {
                Match = "Microsoft.MicrosoftSolitaireCollection",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Game preload."
            },
            new RuleEntry
            {
                Match = "Microsoft.PowerAutomateDesktop",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Automation tool intended for advanced users, not basic customer setup."
            },
            new RuleEntry
            {
                Match = "Microsoft.StartExperiencesApp",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Promotional/first-run Start experience component."
            },
            new RuleEntry
            {
                Match = "Microsoft.Todos",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Optional productivity app; can be installed later if wanted."
            },
            new RuleEntry
            {
                Match = "Microsoft.Windows.DevHome",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Developer-focused app; not needed for basic customers."
            },
            new RuleEntry
            {
                Match = "Microsoft.WindowsFeedbackHub",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Feedback/reporting app; not needed for basic customer setup."
            },
            new RuleEntry
            {
                Match = "MSTeams",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Consumer Teams preload; customers can install Teams later if needed."
            },
            // Samsung: keep-exceptions must stay ahead of the broad catch-all below —
            // FindRule returns the first match, so order here is load-bearing. Both
            // spaced and unspaced forms are listed because manifest-resolved display
            // names are used verbatim (no auto-spacing) — only the CleanPackageName
            // fallback path inserts spaces — so which form actually appears can't be
            // predicted without a real device. The reference script hedges the same
            // way (*ColorEngine* and *Color*Engine* are both keep-patterns there).
            new RuleEntry
            {
                Match = "Color Engine",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display calibration tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "ColorEngine",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display calibration tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Display Profile",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display profile tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "DisplayProfile",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display profile tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Quick Share",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung file-sharing tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "QuickShare",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung file-sharing tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Samsung",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Samsung Appx bloatware removed per shop policy, except Color Engine/Display Profile/Quick Share above."
            },
        ],
        Programs =
        [
            new RuleEntry
            {
                Match = "Copilot",
                MatchType = "exact",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Newer Copilot builds may install as a machine-wide app instead of only Appx."
            },
            new RuleEntry
            {
                Match = "Microsoft 365",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove all Microsoft 365 suite/language/proofing entries per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Microsoft OneNote",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove all Microsoft OneNote entries per shop setup notes."
            },
            new RuleEntry
            {
                Match = "OneNote",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove alternate OneNote entries per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Microsoft Whiteboard",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove Microsoft Whiteboard installed-program entries per shop setup notes."
            },
            new RuleEntry
            {
                Match = "Remote Desktop Connection",
                MatchType = "exact",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove Remote Desktop Connection installed-program entry where present."
            },
            new RuleEntry
            {
                Match = "Microsoft Remote Desktop",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Remove Microsoft Remote Desktop installed-program entries where present."
            },
            // Approximation of the script's compound "(365|Office|OneNote) AND (Language Pack|Proofing)"
            // rule — a single contains-match can't express that AND, so these are broader and Warn-flagged.
            new RuleEntry
            {
                Match = "Language Pack",
                MatchType = "contains",
                Preselected = false,
                Warn = true,
                Blocked = false,
                Reason = "Likely an Office/Microsoft 365/OneNote language pack, but this match isn't scoped to those products — verify before removing."
            },
            new RuleEntry
            {
                Match = "Proofing",
                MatchType = "contains",
                Preselected = false,
                Warn = true,
                Blocked = false,
                Reason = "Likely an Office/Microsoft 365/OneNote proofing tool, but this match isn't scoped to those products — verify before removing."
            },
            // Samsung: keep-exceptions must stay ahead of the broad catch-all below —
            // FindRule returns the first match, so order here is load-bearing. Driver/
            // firmware/hardware-support entries are blocked regardless of publisher.
            // Both spaced and unspaced forms are listed for the same reason as the
            // Apps list above — the reference script hedges the same way there too
            // (*Color Engine*/*ColorEngine* etc. are both keep-patterns for programs).
            new RuleEntry
            {
                Match = "Color Engine",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display calibration tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "ColorEngine",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display calibration tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Display Profile",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display profile tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "DisplayProfile",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung display profile tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Quick Share",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung file-sharing tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "QuickShare",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Samsung file-sharing tool — kept per shop policy."
            },
            new RuleEntry
            {
                Match = "Driver",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Hardware driver — never suggested for removal, avoids breaking hardware support."
            },
            new RuleEntry
            {
                Match = "Firmware",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Firmware update component — never suggested for removal."
            },
            new RuleEntry
            {
                Match = "BIOS",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "BIOS-related component — never suggested for removal."
            },
            new RuleEntry
            {
                Match = "UEFI",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "UEFI-related component — never suggested for removal."
            },
            new RuleEntry
            {
                Match = "NVMe",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "NVMe storage driver/tool — never suggested for removal."
            },
            new RuleEntry
            {
                Match = "Chipset",
                MatchType = "contains",
                Preselected = false,
                Warn = false,
                Blocked = true,
                Reason = "Chipset driver/utility — never suggested for removal."
            },
            new RuleEntry
            {
                Match = "Samsung",
                MatchType = "contains",
                Preselected = true,
                Warn = false,
                Blocked = false,
                Reason = "Samsung installed-program bloatware removed per shop policy, except the hardware-support and Color Engine/Display Profile/Quick Share entries above."
            },
        ]
    };
}
