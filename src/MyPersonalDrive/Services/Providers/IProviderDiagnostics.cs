namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Optional capability: version reporting for a provider backed by an external binary. Present
/// on <see cref="ICloudDriveProvider.Diagnostics"/> only for such providers (Proton's CLI); a
/// provider with no binary to version (Microsoft Graph) leaves this null and the settings UI
/// hides the version/update rows for it (docs/PLAN-CLOUD-PROVIDERS.md §5).
/// </summary>
public interface IProviderDiagnostics
{
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);
}
