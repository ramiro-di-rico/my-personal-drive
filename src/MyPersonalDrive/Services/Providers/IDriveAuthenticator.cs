namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Sign-in/out for a provider. Kept to exactly what the app uses today (Proton's <c>auth
/// login</c>/<c>auth logout</c>); the richer account-identity/token-expiry shape from
/// docs/PLAN-CLOUD-PROVIDERS.md §2.3 is introduced in P6 when OneDrive actually needs it — adding
/// it now would be an abstraction with no second implementation to validate it against.
/// </summary>
public interface IDriveAuthenticator
{
    Task AuthenticateAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);
}
