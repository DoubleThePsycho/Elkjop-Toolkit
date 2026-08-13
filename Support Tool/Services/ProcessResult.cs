namespace Support_Tool.Services;

public record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
