namespace MyPersonalDrive.Services;

public sealed class AppSettings
{
    public string CliPath { get; set; } = string.Empty;

    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// When true, a `filesystem list --json` response that isn't valid JSON is treated as a
    /// hard error instead of falling back to a best-effort text parser. Defaults to false
    /// until docs/PLAN-LOCAL-SYNC.md Phase 0 confirms the CLI reliably honors --json, at
    /// which point the default should flip to true.
    /// </summary>
    public bool StrictListingParsing { get; set; }
}
