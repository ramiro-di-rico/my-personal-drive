namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// What providers exist and how to build the active one — the composition root's single point
/// of provider-specific branching (docs/PLAN-CLOUD-PROVIDERS.md §2.7/P5), so adding a second
/// provider means adding a case here, not touching every consumer that already depends on
/// <see cref="ICloudDriveProvider"/>.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>What the settings view's provider picker lists. Only ever contains providers this
    /// build can actually construct — never a placeholder for one that doesn't exist yet.</summary>
    IReadOnlyList<ProviderDescriptor> Available { get; }

    /// <summary>
    /// Builds the provider for <paramref name="id"/>. Throws <see cref="NotSupportedException"/>
    /// for an id not in <see cref="Available"/> — the app has exactly one active provider at a
    /// time (docs/PLAN-CLOUD-PROVIDERS.md P7 is the optional exception), so an unbuildable id
    /// reaching here is a bug in whatever chose it, not a case to degrade gracefully from.
    /// </summary>
    ICloudDriveProvider Create(ProviderId id, AppSettingsService settings);
}
