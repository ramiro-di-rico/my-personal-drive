namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// A provider's rules for building and comparing remote paths — the pieces of
/// <c>Providers.Proton.ProtonDriveService.CombinePath</c>/<c>HasUnmappableName</c> that were
/// hard-coded Proton assumptions everywhere else in the app. See docs/PLAN-CLOUD-PROVIDERS.md P3
/// and §2.4 for the fuller design (<c>Root</c>/<c>GetParent</c> stay deferred — nothing needs them
/// yet; <see cref="IsLocalNameMappableRemotely"/> landed in P6, once OneDrive's upload path was the
/// thing that actually needed it).
/// </summary>
public interface IProviderPathSyntax
{
    /// <summary>Builds a remote path by appending <paramref name="name"/> under <paramref name="parentPath"/>, escaping whatever this provider's syntax requires.</summary>
    string Combine(string parentPath, string name);

    /// <summary>True when a remote node's name contains a character that makes it unrepresentable as a local file.</summary>
    bool IsRemoteNameMappableLocally(string name);

    /// <summary>
    /// True when a local file's name can be uploaded under that exact name — the upload-side
    /// counterpart to <see cref="IsRemoteNameMappableLocally"/>. Proton accepts anything a local
    /// filesystem already allowed to exist, so its implementation is <c>true</c> unconditionally;
    /// OneDrive rejects a real set of reserved characters and names (docs/PLAN-CLOUD-PROVIDERS.md
    /// §4.6). Not yet consulted anywhere in the sync engine — <c>LocalScanner</c> has no provider
    /// dependency to call it through — so today an unmappable local name uploaded to OneDrive still
    /// surfaces as a raw Graph error rather than a clean skip; tracked as follow-up work rather than
    /// widening this change to restructure <c>LocalScanner</c>.
    /// </summary>
    bool IsLocalNameMappableRemotely(string name);

    /// <summary>
    /// How this provider compares node names. Proton and Linux filenames are both
    /// case-sensitive (<see cref="StringComparison.Ordinal"/>); OneDrive is not. The sync
    /// engine's own path dictionaries stay <see cref="StringComparison.Ordinal"/> regardless
    /// (docs/PLAN-CLOUD-PROVIDERS.md §2.4) — this is only for detecting a case-collision the
    /// remote side considers one node but the local side would split into two.
    /// </summary>
    StringComparison Comparison { get; }

    /// <summary>
    /// True when this provider's backend can hold two siblings with the exact same name in the
    /// same parent, distinguished only by an internal id — Google Drive (docs/PLAN-CLOUD-PROVIDERS.md
    /// §8.2/G2). Defaulted to <c>false</c> so Proton and OneDrive (and any test fake implementing
    /// this interface before this member existed) need no change. <see cref="Sync.RemoteScanner"/>
    /// treats this the same way it already treats a case-insensitive <see cref="Comparison"/>: every
    /// member of a same-name sibling group is skipped and reported
    /// (<see cref="Sync.NodeSkipReason.DuplicateName"/>), never silently merged or overwritten.
    /// </summary>
    bool AllowsDuplicateNamesInSameParent => false;
}
