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
    public void Available_ListsExactlyProton()
    {
        var sut = new ProviderCatalog();

        var descriptor = Assert.Single(sut.Available);
        Assert.Equal(ProviderId.Proton, descriptor.Id);
        Assert.Equal("Proton Drive", descriptor.DisplayName);
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
    public void Create_UnknownProvider_ThrowsRatherThanGuessing()
    {
        var sut = new ProviderCatalog();
        var settings = new AppSettingsService();

        Assert.Throws<NotSupportedException>(() => sut.Create(ProviderId.OneDrive, settings));
    }

    /// <summary>
    /// Regression test for the P1-P5 adversarial review: `ProviderId.OneDrive` is a real,
    /// already-defined enum value (added in P1, ahead of P6's implementation), so
    /// `AppSettings.ActiveProviderOrDefault`'s `Enum.TryParse` alone can't catch it — it parses
    /// fine. Without `ResolveOrDefault`, a settings.json with `"ActiveProvider": "OneDrive"`
    /// would reach `Create` and crash the app at startup instead of degrading to Proton.
    /// </summary>
    [Fact]
    public void ResolveOrDefault_ARealButUnbuildableId_DegradesToTheCatalogsDefault()
    {
        var sut = new ProviderCatalog();

        Assert.Equal(ProviderId.Proton, sut.ResolveOrDefault(ProviderId.OneDrive));
    }

    [Fact]
    public void ResolveOrDefault_ABuildableId_ReturnsItUnchanged()
    {
        var sut = new ProviderCatalog();

        Assert.Equal(ProviderId.Proton, sut.ResolveOrDefault(ProviderId.Proton));
    }
}
