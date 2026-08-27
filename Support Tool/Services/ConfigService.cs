using System.IO;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

// Single owner of config.json. RuleService and InstallCatalogService each read
// their own section (Debloater / Installer) from here instead of independently
// hitting the file — two independent JsonConfigStore writers on the same path
// would each serialize only their own DTO and silently wipe the other's section.
// Now that section, spans all four sections of the shared UnifiedToolkitConfig
// (Account/Connection are Account Setup's, read-but-not-owned here) instead of
// just Debloater/Installer - see Toolkit.Shared.Configuration.UnifiedToolkitConfig's
// own remarks for why there can only be one root type.
//
// ConfigPath deliberately UNCHANGED in Phase 1 - still the old per-tool portable
// path (<drive>:\Elkjop Toolkit\Support Tool\config.json), not the final unified
// <drive>:\Elkjop Toolkit\config.json (Toolkit.Shared.ToolkitPaths.GetPortableConfigPath)
// or the staged Runtime\config.json. service.exe doesn't exist yet, so nothing
// guarantees either of those is populated - repointing here now would break the
// currently-shipping Support Tool.bat/USB path. Deserializing the OLD SupportToolConfig-
// shaped JSON (just Debloater/Installer keys) into UnifiedToolkitConfig works with zero
// extra code: System.Text.Json leaves Account/Connection at their own default
// initializers when those keys are simply absent from the file. This path only moves
// to the staged Runtime copy at the Service Launcher/staging cutover (a later phase).
public class ConfigService
{
    private const string ToolName = "Support Tool";

    private static readonly string ConfigPath =
        Path.Combine(ToolkitPaths.GetConfigFolder(ToolName), "config.json");

    public Task<UnifiedToolkitConfig> LoadAsync() =>
        JsonConfigStore.LoadOrCreateAsync(ConfigPath, CreateDefaultConfig);

    private static UnifiedToolkitConfig CreateDefaultConfig() => new()
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
                    new InstallableApp { Name = "Battle.net", WingetId = "Blizzard.BattleNet", Link = "https://battle.net/" },
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
