using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Support_Tool.Services;

public static class UninstallCommandParser
{
    private static readonly Regex ExePattern = new(
        @"^(.*?\.(?:exe|cmd|bat|msi))\s*(.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Splits a raw UninstallString registry value into (FileName, Arguments).
    /// Handles quoted paths, unquoted paths with extensions, and env variable expansion.
    /// </summary>
    public static (string? FileName, string Arguments) Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (null, "");

        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (commandLine.StartsWith('"'))
        {
            int endQuote = commandLine.IndexOf('"', 1);
            if (endQuote < 1)
                return (null, ""); // malformed quoted string

            string fileName = commandLine.Substring(1, endQuote - 1);
            string args = endQuote + 1 < commandLine.Length
                ? commandLine.Substring(endQuote + 1).Trim()
                : "";

            return (fileName, args);
        }

        var match = ExePattern.Match(commandLine);
        if (match.Success)
            return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());

        // No recognised extension — treat entire string as executable, no args
        return (commandLine, "");
    }

    /// <summary>
    /// Builds a ProcessStartInfo suitable for launching a program's registered uninstaller.
    /// Uses UseShellExecute=true so UAC, path lookup, and GUI windows work correctly.
    /// </summary>
    public static ProcessStartInfo? BuildStartInfo(string? commandLine)
    {
        var (fileName, arguments) = Parse(commandLine);

        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }
}
