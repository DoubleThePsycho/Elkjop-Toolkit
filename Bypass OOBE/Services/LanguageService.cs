using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Bypass_OOBE.Models;
using Microsoft.Win32;

namespace Bypass_OOBE.Services;

internal static partial class LanguageService
{
    // StringBuilder marshalling isn't supported by the LibraryImport source generator, so this stays a classic DllImport.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSystemDefaultLocaleName")]
    private static extern int GetSystemDefaultLocaleNameNative(StringBuilder lpLocaleName, int cchLocaleName);

    [GeneratedRegex(@"^\s*Installed language\(s\)\s*:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex InstalledLanguageRegex();

    public static InternationalInfo GetInternationalInformation(Logger logger)
    {
        string? uiLanguage = null;
        string? systemLocale = null;

        try
        {
            uiLanguage = CultureInfo.InstalledUICulture.Name;
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not read InstalledUICulture: {ex.Message}");
        }

        try
        {
            systemLocale = GetWinSystemLocale();
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not read system locale: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(uiLanguage))
        {
            uiLanguage = CultureInfo.CurrentCulture.Name;
        }

        if (string.IsNullOrWhiteSpace(systemLocale))
        {
            systemLocale = uiLanguage;
        }

        return new InternationalInfo { UiLanguage = uiLanguage, SystemLocale = systemLocale };
    }

    public static LanguageInfo GetAvailableUiLanguages(string currentUiLanguage, Logger logger)
    {
        var detected = new List<string>();
        var sources = new List<string>();

        try
        {
            var dismPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
            if (File.Exists(dismPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = dismPath,
                    Arguments = "/Online /Get-Intl /English",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();

                foreach (Match match in InstalledLanguageRegex().Matches(output))
                {
                    var tag = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(tag) && !detected.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        detected.Add(tag);
                    }
                }

                if (detected.Count > 0)
                {
                    sources.Add($"DISM (exit code {process.ExitCode})");
                }
                else if (process.ExitCode != 0)
                {
                    logger.Warn($"DISM /Online /Get-Intl returned exit code {process.ExitCode}.");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not enumerate display languages through DISM: {ex.Message}");
        }

        if (detected.Count == 0)
        {
            try
            {
                using var muiKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MUI\UILanguages");
                if (muiKey is not null)
                {
                    foreach (var tag in muiKey.GetSubKeyNames())
                    {
                        if (!string.IsNullOrWhiteSpace(tag) && !detected.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            detected.Add(tag);
                        }
                    }

                    if (detected.Count > 0)
                    {
                        sources.Add("MUI registry");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not enumerate display languages from the MUI registry: {ex.Message}");
            }
        }

        if (detected.Count == 0 && !string.IsNullOrWhiteSpace(currentUiLanguage))
        {
            detected.Add(currentUiLanguage);
            sources.Add("Current UI language fallback");
        }

        if (detected.Count == 0)
        {
            detected.Add("nb-NO");
            sources.Add("Hardcoded emergency fallback");
        }

        var languageItems = new List<LanguageItem>();
        foreach (var tag in detected)
        {
            string normalizedTag;
            try
            {
                normalizedTag = CultureInfo.GetCultureInfo(tag).Name;
            }
            catch
            {
                normalizedTag = tag;
            }

            languageItems.Add(new LanguageItem { Tag = normalizedTag, DisplayName = GetLanguageDisplayName(normalizedTag) });
        }

        languageItems = languageItems
            .GroupBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var norwegian = languageItems.FirstOrDefault(l => string.Equals(l.Tag, "nb-NO", StringComparison.OrdinalIgnoreCase));
        var current = languageItems.FirstOrDefault(l => string.Equals(l.Tag, currentUiLanguage, StringComparison.OrdinalIgnoreCase));
        var defaultTag = norwegian?.Tag ?? current?.Tag ?? languageItems[0].Tag;

        return new LanguageInfo
        {
            Languages = languageItems,
            DefaultTag = defaultTag,
            DetectionMode = string.Join(", ", sources),
        };
    }

    private static string GetLanguageDisplayName(string languageTag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageTag);
            var nativeName = culture.NativeName;

            if (string.IsNullOrWhiteSpace(nativeName))
            {
                nativeName = culture.DisplayName;
            }

            return $"{nativeName} [{culture.Name}]";
        }
        catch
        {
            return languageTag;
        }
    }

    private static string? GetWinSystemLocale()
    {
        var buffer = new StringBuilder(85);
        var length = GetSystemDefaultLocaleNameNative(buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : null;
    }
}
