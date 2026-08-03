using Xunit;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// A test that talks to the real `proton-drive` CLI and a real authenticated Proton account.
/// Skipped unless <c>MYPERSONALDRIVE_INTEGRATION=1</c>, because it is slow (every CLI call costs
/// ~3.5s of process startup — docs/PLAN-LOCAL-SYNC.md Appendix A #11a), needs an interactive
/// `auth login` beforehand, and creates and trashes real remote folders.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MYPERSONALDRIVE_INTEGRATION";

    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 (and authenticate the CLI) to run integration tests.";
        }
    }
}
