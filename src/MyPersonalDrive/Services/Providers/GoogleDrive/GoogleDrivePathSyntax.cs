namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Google Drive's path and naming rules — docs/PLAN-CLOUD-PROVIDERS.md §8.2/§8.6 (G2/G6). Drive has
/// no native path at all (every node is addressed by an opaque id — see
/// <see cref="GoogleDriveOperations"/>'s path→id resolution) and essentially no upload-side name
/// restriction, the mirror image of <c>OneDrive.OneDrivePathSyntax</c>'s restrictive §4.6/O6 rules.
/// </summary>
public sealed class GoogleDrivePathSyntax : IProviderPathSyntax
{
    public string Combine(string parentPath, string name)
        => string.IsNullOrEmpty(parentPath) || parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";

    /// <summary>Only '/' makes a remote name unrepresentable as a single local path segment — same rule as Proton/OneDrive.</summary>
    public bool IsRemoteNameMappableLocally(string name) => !name.Contains('/');

    /// <summary>
    /// Drive has essentially no upload-side name restriction (confirmed against Google's published
    /// docs during Phase 1's research pass — §8's own G6 note) — the only thing that would ever make
    /// a local name unrepresentable remotely is the same '/' restriction the download side already
    /// enforces symmetrically.
    /// </summary>
    public bool IsLocalNameMappableRemotely(string name) => !string.IsNullOrEmpty(name) && !name.Contains('/');

    /// <summary>Drive names are case-sensitive and case-preserving — like Proton/Linux, unlike OneDrive.</summary>
    public StringComparison Comparison => StringComparison.Ordinal;

    /// <summary>
    /// Drive's own File resource docs state explicitly that names are not unique within a folder —
    /// two files can share both a name and a parent, distinguished only by id
    /// (docs/PLAN-CLOUD-PROVIDERS.md §8.2/G2). The one provider this is true for so far.
    /// </summary>
    public bool AllowsDuplicateNamesInSameParent => true;
}
