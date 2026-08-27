namespace Account_Setup.Models;

internal sealed class AppConfig
{
    public AccountConfig Account { get; init; } = new();
    public ConnectionConfig Connection { get; init; } = new();
}

internal sealed class AccountConfig
{
    public string Username { get; init; } = "Bruker";
}

internal sealed class ConnectionConfig
{
    public string WifiName { get; init; } = string.Empty;
    public string WifiPassword { get; init; } = string.Empty;
}
