using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Tests;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers;

[Collection(AppDataCollection.Name)]
public class ProviderCatalogTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ProviderCatalog").FullName;
    private readonly string? _originalAppData;

    public ProviderCatalogTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        Directory.Delete(_tempAppData, recursive: true);
    }

    [Fact]
    public void Available_ListsAllSupportedProviders()
    {
        var sut = new ProviderCatalog();

        Assert.Collection(
            sut.Available,
            proton =>
            {
                Assert.Equal(ProviderId.Proton, proton.Id);
                Assert.Equal("Proton Drive", proton.DisplayName);
            },
            oneDrive =>
            {
                Assert.Equal(ProviderId.OneDrive, oneDrive.Id);
                Assert.Equal("OneDrive", oneDrive.DisplayName);
            },
            google =>
            {
                Assert.Equal(ProviderId.GoogleDrive, google.Id);
                Assert.Equal("Google Drive", google.DisplayName);
            },
            nextcloud =>
            {
                Assert.Equal(ProviderId.Nextcloud, nextcloud.Id);
                Assert.Equal("Nextcloud", nextcloud.DisplayName);
            },
            s3 =>
            {
                Assert.Equal(ProviderId.S3, s3.Id);
                Assert.Equal("Custom S3", s3.DisplayName);
            });
    }

    [Fact]
    public void Create_Proton_ReturnsAWorkingProtonProvider()
    {
        var sut = new ProviderCatalog();
        var settings = new AppSettingsService();

        var provider = sut.Create(ProviderId.Proton, settings);

        Assert.Equal(ProviderId.Proton, provider.Id);
        Assert.IsType<ProtonDriveProvider>(provider);
    }

    [Fact]
    public void Create_OneDrive_ReturnsAWorkingOneDriveProvider()
    {
        var sut = new ProviderCatalog();
        var settings = new AppSettingsService();

        var provider = sut.Create(ProviderId.OneDrive, settings);

        Assert.Equal(ProviderId.OneDrive, provider.Id);
        Assert.IsType<MyPersonalDrive.Services.Providers.OneDrive.OneDriveProvider>(provider);
    }

    [Fact]
    public void Create_GoogleDrive_ReturnsAWorkingGoogleDriveProvider_NotTheGenericStub()
    {
        var sut = new ProviderCatalog();
        var settings = new AppSettingsService();

        var provider = sut.Create(ProviderId.GoogleDrive, settings);

        Assert.Equal(ProviderId.GoogleDrive, provider.Id);
        Assert.IsType<MyPersonalDrive.Services.Providers.GoogleDrive.GoogleDriveProvider>(provider);
    }

    [Fact]
    public void Create_UnknownProvider_ThrowsRatherThanGuessing()
    {
        var sut = new ProviderCatalog();
        var settings = new AppSettingsService();

        Assert.Throws<NotSupportedException>(() => sut.Create((ProviderId)99, settings));
    }

    /// <summary>
    /// Regression test for the P1-P5 adversarial review, updated for P6: the original gap was
    /// `ProviderId.OneDrive` being a real, already-defined enum value (added in P1, ahead of P6's
    /// implementation) with no catalog entry, so `AppSettings.ActiveProviderOrDefault`'s
    /// `Enum.TryParse` alone couldn't catch it — it parsed fine, and `Create` crashed the app at
    /// startup instead of degrading. Now that OneDrive is genuinely registered, the same coverage
    /// is exercised with a synthetic out-of-range value instead: `ResolveOrDefault` must still
    /// degrade to the catalog's default for any id `Create` can't build, not only OneDrive.
    /// </summary>
    [Fact]
    public void ResolveOrDefault_ARealButUnbuildableId_DegradesToTheCatalogsDefault()
    {
        var sut = new ProviderCatalog();

        Assert.Equal(ProviderId.Proton, sut.ResolveOrDefault((ProviderId)99));
    }

    [Fact]
    public void ResolveOrDefault_ABuildableId_ReturnsItUnchanged()
    {
        var sut = new ProviderCatalog();

        Assert.Equal(ProviderId.Proton, sut.ResolveOrDefault(ProviderId.Proton));
        Assert.Equal(ProviderId.OneDrive, sut.ResolveOrDefault(ProviderId.OneDrive));
        Assert.Equal(ProviderId.GoogleDrive, sut.ResolveOrDefault(ProviderId.GoogleDrive));
    }
}
