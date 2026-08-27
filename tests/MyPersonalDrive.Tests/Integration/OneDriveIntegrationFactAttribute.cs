using Xunit;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// A test that talks to the real Microsoft Graph API and a real OneDrive account. Skipped unless
/// <c>MYPERSONALDRIVE_ONEDRIVE_INTEGRATION=1</c> and <c>MYPERSONALDRIVE_ONEDRIVE_CLIENT_ID</c> are
/// both set — it needs an interactive sign-in (opens a browser, blocks on a loopback redirect) and
/// creates/trashes a real file in the account's root. This is also this phase's
/// live-verification harness (docs/PLAN-CLOUD-PROVIDERS.md P6): its result is what populates
/// Appendix A.
/// </summary>
public sealed class OneDriveIntegrationFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MYPERSONALDRIVE_ONEDRIVE_INTEGRATION";
    public const string ClientIdEnvironmentVariable = "MYPERSONALDRIVE_ONEDRIVE_CLIENT_ID";

    public OneDriveIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 and {ClientIdEnvironmentVariable} to run this test.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable)))
        {
            Skip = $"{EnvironmentVariable}=1 but {ClientIdEnvironmentVariable} is not set.";
        }
    }
}
