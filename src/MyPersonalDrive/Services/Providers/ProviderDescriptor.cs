namespace MyPersonalDrive.Services.Providers;

/// <summary>What the settings view's provider picker lists — see <see cref="IProviderCatalog"/>.</summary>
public sealed record ProviderDescriptor(ProviderId Id, string DisplayName);
