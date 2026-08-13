namespace Bypass_OOBE.Models;

internal sealed record UsernameValidationResult(bool Valid, string Username, string? Error);
