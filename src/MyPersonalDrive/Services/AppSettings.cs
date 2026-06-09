namespace MyPersonalDrive.Services;

public sealed class AppSettings
{
    public string CliPath { get; set; } = string.Empty;

    public bool IsAuthenticated { get; set; }
}
