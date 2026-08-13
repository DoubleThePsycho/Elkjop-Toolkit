namespace Support_Tool.Services;

public interface IRestorePointService
{
    Task<(bool Success, string? ErrorMessage)> CreateAsync(string description);
}
