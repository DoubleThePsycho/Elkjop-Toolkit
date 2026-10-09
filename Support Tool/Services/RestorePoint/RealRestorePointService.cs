namespace Support_Tool.Services;

public class RealRestorePointService : IRestorePointService
{
    public Task<(bool Success, string? ErrorMessage)> CreateAsync(string description)
    {
        throw new NotImplementedException(
            "Real restore point creation is intentionally disabled during development.");
    }
}
