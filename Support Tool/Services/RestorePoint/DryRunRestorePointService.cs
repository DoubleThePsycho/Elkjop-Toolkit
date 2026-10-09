using System.Diagnostics;

namespace Support_Tool.Services;

public class DryRunRestorePointService : IRestorePointService
{
    public async Task<(bool Success, string? ErrorMessage)> CreateAsync(string description)
    {
        Debug.WriteLine($"[DRY RUN] Would create restore point: {description}");
        await Task.Delay(200);
        return (true, null);
    }
}
