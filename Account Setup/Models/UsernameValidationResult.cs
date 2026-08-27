namespace Account_Setup.Models;

internal sealed record UsernameValidationResult(bool Valid, string Username, string? Error);
