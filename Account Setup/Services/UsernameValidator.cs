using System.Diagnostics;
using System.Text.RegularExpressions;
using Account_Setup.Models;

namespace Account_Setup.Services;

internal static partial class UsernameValidator
{
    [GeneratedRegex(@"[/\\\[\]:|<>+=;,?*%@""]")]
    private static partial Regex InvalidCharactersRegex();

    public static UsernameValidationResult Validate(string username, Logger logger)
    {
        var trimmed = username.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new UsernameValidationResult(false, trimmed, "Enter a username.");
        }

        if (trimmed.Length > 20)
        {
            return new UsernameValidationResult(false, trimmed, "The username must be 20 characters or fewer for broad Windows compatibility.");
        }

        if (InvalidCharactersRegex().IsMatch(trimmed))
        {
            return new UsernameValidationResult(false, trimmed, "The username contains a character Windows does not allow.");
        }

        if (string.Equals(trimmed, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return new UsernameValidationResult(false, trimmed, "NONE is a reserved Windows username.");
        }

        if (trimmed.All(c => c == '.') || trimmed.EndsWith('.'))
        {
            return new UsernameValidationResult(false, trimmed, "The username cannot consist only of periods or end with a period.");
        }

        try
        {
            if (LocalAccountExists(trimmed))
            {
                return new UsernameValidationResult(false, trimmed, $"A local account named '{trimmed}' already exists.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not check existing local accounts: {ex.Message}");
        }

        return new UsernameValidationResult(true, trimmed, null);
    }

    private static bool LocalAccountExists(string username)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe"),
            Arguments = $"user \"{username}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0;
    }
}
