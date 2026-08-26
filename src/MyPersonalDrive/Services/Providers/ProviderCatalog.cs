namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// The real catalog: only Proton exists today, so this is exactly the composition-root wiring
/// that used to sit inline in <c>App.axaml.cs</c>, moved here so a settings-view provider picker
/// has something to enumerate (docs/PLAN-CLOUD-PROVIDERS.md P5) and so P6 has one place to add
/// OneDrive's case instead of a second copy of this branching.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog
{
    public IReadOnlyList<ProviderDescriptor> Available { get; } =
        [new ProviderDescriptor(ProviderId.Proton, "Proton Drive")];

    public ICloudDriveProvider Create(ProviderId id, AppSettingsService settings)
        => id switch
        {
            ProviderId.Proton => CreateProton(settings),
            _ => throw new NotSupportedException($"Provider '{id}' is not available yet.")
        };

    private static Proton.ProtonDriveProvider CreateProton(AppSettingsService settings)
    {
        var locator = new Proton.ProtonDriveCliLocator(settings);
        var executor = new Proton.ProtonDriveCliExecutor(locator);
        var service = new Proton.ProtonDriveService(executor, settings.Load().StrictListingParsing);
        return new Proton.ProtonDriveProvider(service);
    }
}
