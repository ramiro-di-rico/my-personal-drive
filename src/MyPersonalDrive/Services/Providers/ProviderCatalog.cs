using MyPersonalDrive.Services.Providers.Generic;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// The provider catalog registering and resolving cloud drive backends:
/// Proton Drive, OneDrive, Google Drive, Nextcloud, and Custom S3.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog
{
    public IReadOnlyList<ProviderDescriptor> Available { get; } =
    [
        new ProviderDescriptor(ProviderId.Proton, "Proton Drive"),
        new ProviderDescriptor(ProviderId.OneDrive, "OneDrive"),
        new ProviderDescriptor(ProviderId.GoogleDrive, "Google Drive"),
        new ProviderDescriptor(ProviderId.Nextcloud, "Nextcloud"),
        new ProviderDescriptor(ProviderId.S3, "Custom S3")
    ];

    public ICloudDriveProvider Create(ProviderId id, AppSettingsService settings)
        => id switch
        {
            ProviderId.Proton => CreateProton(settings),
            ProviderId.OneDrive => CreateOneDrive(settings),
            ProviderId.GoogleDrive => new GenericCloudDriveProvider(ProviderId.GoogleDrive, "Google Drive"),
            ProviderId.Nextcloud => new GenericCloudDriveProvider(ProviderId.Nextcloud, "Nextcloud"),
            ProviderId.S3 => new GenericCloudDriveProvider(ProviderId.S3, "Custom S3"),
            _ => throw new NotSupportedException($"Provider '{id}' is not available yet.")
        };

    public ProviderId ResolveOrDefault(ProviderId requested)
        => Available.Any(descriptor => descriptor.Id == requested) ? requested : Available[0].Id;

    private static Proton.ProtonDriveProvider CreateProton(AppSettingsService settings)
    {
        var locator = new Proton.ProtonDriveCliLocator(settings);
        var executor = new Proton.ProtonDriveCliExecutor(locator);
        var service = new Proton.ProtonDriveService(executor, settings.Load().StrictListingParsing);
        return new Proton.ProtonDriveProvider(service);
    }

    private static OneDrive.OneDriveProvider CreateOneDrive(AppSettingsService settings)
    {
        var appSettings = settings.Load();
        var tokenStore = new OneDrive.OneDriveTokenStore(settings.BaseFolder);
        var authenticator = new OneDrive.GraphAuthenticator(appSettings.OneDriveClientId, tokenStore);
        var http = new OneDrive.GraphHttpClient(authenticator);
        return new OneDrive.OneDriveProvider(authenticator, http);
    }
}
