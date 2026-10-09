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
// Support Tool reads the staged RuntimeStaging.ConfigPath
// (C:\ProgramData\Elkjop\Toolkit\Runtime\config.json) strictly - missing or corrupt fails
// visibly (ConfigLoadException), never silently regenerates defaults, in both DEBUG and RELEASE.
// The Elkjop Toolkit.exe dispatcher/Account Setup staging populate that path. Phase 5 removed the
// DEBUG-only legacy per-tool USB-path fallback this comment used to describe, alongside the
// legacy Support Tool.bat direct-from-USB launcher it existed to support.
public class ConfigService
{
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

        if (config is null)
        {
            throw new ConfigLoadException($"Configuration file '{path}' deserialized to nothing.");
        }

        // Additive, in-memory-only migration for a real physical config.json that predates this
        // pass - see EnsureRequiredCatalogEntries's own remarks for exactly what it will and won't
        // touch. Runs BEFORE the SetupPresets synthesis below so a freshly-synthesized default
        // preset's WingetIds (Java 8, Discord, etc.) can actually resolve against the catalog on
        // the very first load, not just after a technician manually re-saves config.json.
        EnsureRequiredCatalogEntries(config.Installer);

        // Additive/optional (see InstallerConfig.SetupPresets remarks) - an older physical USB
        // config.json written before this feature existed has no SetupPresets section at all, and
        // must keep loading exactly as before. Synthesized in memory only, never written back to
        // disk as a side effect of loading.
        if (config.Installer.SetupPresets.Count == 0)
        {
            config.Installer.SetupPresets = CreateDefaultSetupPresets();
        }
        else
        {
            // A NARROWER case than the one above (external review): SetupPresets already exist -
            // e.g. a config staged between the SA Gaming preset's own introduction and its later
            // CategoryNames addition - and would otherwise silently never gain Game Launchers. See
            // MigrateLegacySetupPresets's own remarks for exactly what this will and won't touch.
            MigrateLegacySetupPresets(config.Installer.SetupPresets, json);
        }

        return config;
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

    // Phase 5: the DEBUG-only legacy USB-relative fallback that used to live here (for running
    // Support Tool directly from the USB via the now-deleted Support Tool.bat) is gone - Support
    // Tool always runs from a staged Runtime now, in both DEBUG and RELEASE.
    private static string ResolveConfigPath() => RuntimeStaging.ConfigPath;

    // Kept public so the Phase 2B dev-scripts test-staging helper (and, later, Service
    // Launcher/Account Setup staging) can seed a real config.json without duplicating this
    // catalog data - LoadAsync itself no longer calls this on a missing file (see class remarks).
    public static UnifiedToolkitConfig CreateDefaultConfig() => new()
    {
        Debloater = CreateDefaultRuleSet(),
        Installer = CreateDefaultInstallerConfig()
    };

    // The one shared winget package ID for both Java 8 catalog rows below - see their own remarks
    // for why Eclipse Adoptium/Temurin was not used here.
    private const string Java8WingetId = "Amazon.Corretto.8.JRE";

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
            },
            ["Utilities"] = new InstallCategoryConfig
            {
                InstallAll = false,
                Apps = CreateUtilitiesApps()
            },
            ["Communication"] = new InstallCategoryConfig
            {
                InstallAll = false,
                Apps = CreateCommunicationApps()
            }
        },
        SetupPresets = CreateDefaultSetupPresets()
    };

    // The Java 8 rows are the one genuinely NEW Utilities entry as of this pass - extracted so
    // both the full default Utilities set below AND the narrower legacy-migration set (see
    // CreateRequiredLegacyUtilityAdditions) share one definition rather than duplicating it.
    private static List<InstallableApp> CreateJava8Apps() =>
    [
        // Eclipse Adoptium/Temurin 8 is x64-only in winget's "winget" source (verified:
        // `winget show --id EclipseAdoptium.Temurin.8.JRE --architecture x86` -> "No
        // applicable installer found") - no x86 alternative exists under any Temurin 8
        // ID. Amazon Corretto's 8.JRE package genuinely supports both architectures
        // under one ID via --architecture (verified: distinct real installer URLs
        // returned for x86 vs x64), so it is used here instead for both rows. Both rows
        // share the same WingetId by design - see InstallableApp.Architecture's own
        // remarks for the one known limitation this creates for status checking.
        new InstallableApp { Name = "Java 8 (32-bit)", WingetId = Java8WingetId, Architecture = "x86", Link = "https://aws.amazon.com/corretto/" },
        new InstallableApp { Name = "Java 8 (64-bit)", WingetId = Java8WingetId, Architecture = "x64", Link = "https://aws.amazon.com/corretto/" },
    ];

    // The complete default Utilities category - used by CreateDefaultInstallerConfig for a
    // genuinely fresh config only. Spotify/VLC are NOT part of the legacy-migration required set
    // below (see its own remarks) - they predate this pass and must never be resurrected into a
    // deployed config that intentionally omits them.
    private static List<InstallableApp> CreateUtilitiesApps() =>
    [
        new InstallableApp { Name = "Spotify", WingetId = "Spotify.Spotify", Link = "https://www.spotify.com/" },
        new InstallableApp { Name = "VLC media player", WingetId = "VideoLAN.VLC", Link = "https://www.videolan.org/vlc/" },
        .. CreateJava8Apps(),
    ];

    private static List<InstallableApp> CreateCommunicationApps() =>
    [
        // Skype is retired/obsolete and deliberately not included. WhatsApp has no
        // "winget" source listing at all (only msstore, ID 9NKSQGP7F2NH) and is
        // deliberately excluded here to keep this catalog entirely community-source,
        // matching every other entry's licensing/sign-in semantics.
        new InstallableApp { Name = "Discord", WingetId = "Discord.Discord", Link = "https://discord.com/" },
        new InstallableApp { Name = "Microsoft Teams", WingetId = "Microsoft.Teams", Link = "https://www.microsoft.com/microsoft-teams/" },
        new InstallableApp { Name = "Zoom Workplace", WingetId = "Zoom.Zoom", Link = "https://zoom.us/" },
        new InstallableApp { Name = "Signal", WingetId = "OpenWhisperSystems.Signal", Link = "https://signal.org/" },
        new InstallableApp { Name = "Telegram Desktop", WingetId = "Telegram.TelegramDesktop", Link = "https://telegram.org/" },
    ];

    // Narrower than CreateUtilitiesApps on purpose (external review correction) - Spotify and VLC
    // are NOT new entries introduced by this feature, so an intentionally-customized old config
    // that removed either of them must never have them silently resurrected by this migration.
    // Only the Java 8 rows are genuinely required-new for Utilities.
    private static List<InstallableApp> CreateRequiredLegacyUtilityAdditions() => CreateJava8Apps();

    // Communication is an entirely new category as of this pass, so its full default app list IS
    // the correct required-legacy set - no narrowing needed here, unlike Utilities.
    private static List<InstallableApp> CreateRequiredLegacyCommunicationAdditions() => CreateCommunicationApps();

    // Additive, idempotent, in-memory-only catalog migration for a real physical config.json that
    // predates this pass (see LoadAsync). Never replaces a whole category, never overwrites an
    // existing app's fields (Battle.net's InstallLocation, any technician-edited Preselected/Link/
    // name, category ordering - all left completely alone), and never re-seeds anything already
    // present or anything not genuinely new to this feature (see the two Create*Additions methods'
    // own remarks) - only ADDS whichever of the required rows are genuinely missing, matched by
    // MatchesRequiredCatalogEntry's identity rule below. Calling this twice on the same config
    // produces no duplicates - identity matching makes it a no-op the second time.
    private static void EnsureRequiredCatalogEntries(InstallerConfig installer)
    {
        EnsureCategoryHasApps(installer, "Utilities", CreateRequiredLegacyUtilityAdditions());
        EnsureCategoryHasApps(installer, "Communication", CreateRequiredLegacyCommunicationAdditions());
    }

    private static void EnsureCategoryHasApps(InstallerConfig installer, string categoryName, List<InstallableApp> requiredApps)
    {
        if (!installer.Categories.TryGetValue(categoryName, out var category))
        {
            category = new InstallCategoryConfig { InstallAll = false, Apps = [] };
            installer.Categories[categoryName] = category;
        }

        foreach (var required in requiredApps)
        {
            if (!category.Apps.Any(existing => MatchesRequiredCatalogEntry(existing, required)))
            {
                category.Apps.Add(required);
            }
        }
    }

    // Asymmetric by design (external review correction): a REQUIRED row with no Architecture
    // (every normal app) matches any EXISTING row with the same WingetId regardless of that
    // existing row's own Architecture value - an unrelated customization (e.g. someone set
    // Architecture on a normal app for an unrelated reason) must never cause a duplicate default
    // row to be appended. A REQUIRED row that DOES have an Architecture (Java 8's x86/x64) matches
    // only an existing row with the same WingetId AND the same Architecture, since those two rows
    // intentionally share one WingetId and only Architecture tells them apart.
    private static bool MatchesRequiredCatalogEntry(InstallableApp existing, InstallableApp required)
    {
        if (!string.Equals(existing.WingetId, required.WingetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrEmpty(required.Architecture)
            || string.Equals(existing.Architecture ?? "", required.Architecture, StringComparison.OrdinalIgnoreCase);
    }

    // SA Standard/SA Gaming (see SetupHomePageViewModel) - presets over this same catalog, never a
    // second install engine. The browser itself is intentionally NOT listed here: Setup Home's own
    // browser selector (default Firefox) supplies whichever WingetId the technician picked, merged
    // in at apply time. SA Gaming is SA Standard's own list plus Discord plus every app currently
    // in the "Game Launchers" category (external review - resolved by category NAME at apply time,
    // not a frozen id list, so a launcher added later automatically participates with no config
    // change here - see SetupPresetConfig.CategoryNames's own remarks). SA Standard deliberately
    // does not reference any category - two explicit lists, no inheritance machinery.
    public static Dictionary<string, SetupPresetConfig> CreateDefaultSetupPresets()
    {
        var standardApps = new List<string> { Java8WingetId, "Spotify.Spotify", "VideoLAN.VLC" };

        return new Dictionary<string, SetupPresetConfig>
        {
            ["SA Standard"] = new SetupPresetConfig { WingetIds = standardApps },
            ["SA Gaming"] = new SetupPresetConfig
            {
                WingetIds = [.. standardApps, "Discord.Discord"],
                CategoryNames = ["Game Launchers"],
            },
        };
    }

    // Narrow, in-memory-only migration for a real physical config.json whose SetupPresets ALREADY
    // exist but predate SA Gaming's CategoryNames addition (external review) - e.g. a config
    // staged between the SA Gaming preset's own introduction and the later Game Launchers
    // addition. Without this, such a config's SA Gaming would silently never gain Game Launchers,
    // even though CreateDefaultSetupPresets() itself now generates that field for a brand-new
    // config - the very inconsistency this migration exists to close.
    //
    // Only ever touches the "SA Gaming" entry, and only when BOTH of these hold:
    //   - its CategoryNames property was genuinely ABSENT from the source JSON (an explicit
    //     "CategoryNames": [] is a real technician/config choice - opting OUT of any category -
    //     and is never overwritten; SetupPresetConfig.CategoryNames's own List<string> = []
    //     initializer makes "absent" and "explicit empty" deserialize identically, which is why
    //     this reads the raw JSON directly rather than trusting the already-deserialized DTO);
    //   - its WingetIds still match exactly what CreateDefaultSetupPresets() itself generates for
    //     SA Gaming, compared as a set (case-insensitive, order-insensitive, duplicate-insensitive)
    //     - a legacy SA Gaming with customized WingetIds is never assumed to want this migration,
    //     since that would silently override technician policy rather than migrate our own prior
    //     default.
    // Idempotent (an already-migrated or already-explicit config is a no-op on every later load)
    // and never touches SA Standard or any other preset name.
    private static void MigrateLegacySetupPresets(Dictionary<string, SetupPresetConfig> presets, string rawJson)
    {
        const string gamingPresetName = "SA Gaming";

        if (!presets.TryGetValue(gamingPresetName, out var gaming))
        {
            return;
        }

        if (WasPropertyPresentInJson(rawJson, "Installer", "SetupPresets", gamingPresetName, "CategoryNames"))
        {
            return;
        }

        var legacyDefaultWingetIds = new HashSet<string>(
            CreateDefaultSetupPresets()[gamingPresetName].WingetIds,
            StringComparer.OrdinalIgnoreCase);
        var actualWingetIds = new HashSet<string>(gaming.WingetIds, StringComparer.OrdinalIgnoreCase);

        if (!actualWingetIds.SetEquals(legacyDefaultWingetIds))
        {
            return;
        }

        gaming.CategoryNames = ["Game Launchers"];
    }

    // Answers "was this exact nested property present in the source JSON", walking `path` one
    // segment at a time - distinguishes an absent property from an explicit empty value/array,
    // which a DTO's own default-initializer cannot distinguish after deserialization. Matches
    // JsonOptions' own PropertyNameCaseInsensitive at every level. Fails safe to false (never
    // throws) for any structural mismatch along the way, or if rawJson itself doesn't parse -
    // for this specific narrow use, "can't tell" must never force the migration above.
    private static bool WasPropertyPresentInJson(string rawJson, params string[] path)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var current = document.RootElement;

            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !TryGetPropertyCaseInsensitive(current, segment, out var next))
                {
                    return false;
                }

                current = next;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // Defaults sourced from the shop's own historical cleanup-script policy (that script
    // is no longer kept in this repo — these rules are the current source of truth).
    // Publisher-scoped and compound (regex AND) rules it used can't be represented by a
    // single Match/MatchType pair here and were approximated — see conversation/memory
    // for specifics on what was simplified.
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
            // predicted without a real device. The shop's own historical cleanup-script
            // policy hedged the same way (*ColorEngine* and *Color*Engine* were both
            // keep-patterns there).
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
            // Apps list above — the shop's own historical cleanup-script policy hedged
            // the same way there too (*Color Engine*/*ColorEngine* etc. were both
            // keep-patterns for programs).
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
