namespace MyPersonalDrive.Services.Providers;

/// <summary>What the settings view's provider picker lists — see <see cref="IProviderCatalog"/>.</summary>
public sealed record ProviderDescriptor(
    ProviderId Id,
    string DisplayName,
    string? AccountIdentity = null,
    bool IsAuthenticated = false)
{
    public string AccountSummary => string.IsNullOrWhiteSpace(AccountIdentity)
        ? (IsAuthenticated ? "Connected" : "Not signed in")
        : AccountIdentity;
}
