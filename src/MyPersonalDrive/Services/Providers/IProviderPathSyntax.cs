namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// A provider's rules for building and comparing remote paths — the pieces of
/// <c>Providers.Proton.ProtonDriveService.CombinePath</c>/<c>HasUnmappableName</c> that were
/// hard-coded Proton assumptions everywhere else in the app. See docs/PLAN-CLOUD-PROVIDERS.md P3
/// and §2.4 for the fuller design (<c>Root</c>, <c>GetParent</c> and
/// <c>IsLocalNameMappableRemotely</c> are deferred to P6, when OneDrive's upload path is what
/// actually needs them — adding them now would be untested surface).
/// </summary>
public interface IProviderPathSyntax
{
    /// <summary>Builds a remote path by appending <paramref name="name"/> under <paramref name="parentPath"/>, escaping whatever this provider's syntax requires.</summary>
    string Combine(string parentPath, string name);

    /// <summary>True when a remote node's name contains a character that makes it unrepresentable as a local file.</summary>
    bool IsRemoteNameMappableLocally(string name);

    /// <summary>
    /// How this provider compares node names. Proton and Linux filenames are both
    /// case-sensitive (<see cref="StringComparison.Ordinal"/>); OneDrive is not. The sync
    /// engine's own path dictionaries stay <see cref="StringComparison.Ordinal"/> regardless
    /// (docs/PLAN-CLOUD-PROVIDERS.md §2.4) — this is only for detecting a case-collision the
    /// remote side considers one node but the local side would split into two.
    /// </summary>
    StringComparison Comparison { get; }
}
