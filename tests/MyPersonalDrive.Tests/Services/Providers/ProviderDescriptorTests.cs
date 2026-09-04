using MyPersonalDrive.Services.Providers;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers;

/// <summary>
/// docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2: the header ComboBox's SelectedItem binding lost
/// track of the current selection when switching directly between two providers, because
/// MainWindowViewModel.AvailableProviders/SelectedProvider rebuild brand-new ProviderDescriptor
/// instances on every access, and the record's default full-property equality made two
/// recomputations of "the same" provider compare unequal whenever AccountIdentity/IsAuthenticated
/// happened to differ between them.
/// </summary>
public sealed class ProviderDescriptorTests
{
    [Fact]
    public void TwoDescriptors_WithTheSameId_AreEqual_EvenWithDifferentLiveFields()
    {
        var a = new ProviderDescriptor(ProviderId.GoogleDrive, "Google Drive", AccountIdentity: null, IsAuthenticated: false);
        var b = new ProviderDescriptor(ProviderId.GoogleDrive, "Google Drive", AccountIdentity: "me@gmail.com", IsAuthenticated: true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TwoDescriptors_WithDifferentIds_AreNotEqual()
    {
        var a = new ProviderDescriptor(ProviderId.Proton, "Proton Drive");
        var b = new ProviderDescriptor(ProviderId.GoogleDrive, "Google Drive");

        Assert.NotEqual(a, b);
    }
}
