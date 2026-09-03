namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Identifies a cloud storage backend. See docs/PLAN-CLOUD-PROVIDERS.md.
/// </summary>
public enum ProviderId
{
    Proton,
    OneDrive,
    GoogleDrive,
    Nextcloud,
    S3
}
