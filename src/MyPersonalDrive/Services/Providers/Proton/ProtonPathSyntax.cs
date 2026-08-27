using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Delegates to <see cref="ProtonDriveService.CombinePath"/>/<see cref="ProtonDriveService.HasUnmappableName"/>,
/// which still hold the real logic (the CLI's own `\/`-escaping convention, verified against a
/// real account — see their doc comments). This type exists so callers depend on
/// <see cref="IProviderPathSyntax"/> instead of the concrete Proton type.
/// </summary>
public sealed class ProtonPathSyntax : IProviderPathSyntax
{
    public string Combine(string parentPath, string name) => ProtonDriveService.CombinePath(parentPath, name);

    public bool IsRemoteNameMappableLocally(string name) => !ProtonDriveService.HasUnmappableName(name);

    /// <summary>Proton has no reserved-name rule of its own; anything a local filesystem allowed to exist is uploadable.</summary>
    public bool IsLocalNameMappableRemotely(string name) => true;

    /// <summary>Proton and Linux filenames are both case-sensitive.</summary>
    public StringComparison Comparison => StringComparison.Ordinal;
}
