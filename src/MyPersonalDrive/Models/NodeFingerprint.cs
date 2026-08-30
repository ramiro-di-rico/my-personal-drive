namespace MyPersonalDrive.Models;

/// <summary>
/// A snapshot of one node (file or folder) on one side (local or remote), comparable across
/// scans. Per docs/PLAN-LOCAL-SYNC.md Appendix A (verified against the real CLI): on the
/// remote side, <see cref="NodeId"/> is the CLI's stable `uid` (survives rename/move) and
/// <see cref="ContentHash"/> is `activeRevision.value.claimedDigests.sha1` when present. On the
/// local side, <see cref="ContentHash"/> is a locally-computed hash so the two sides compare
/// directly, and <see cref="NodeId"/> is null (local identity is the inode, tracked separately
/// in SyncState.LocalInode — see §11).
/// </summary>
/// <param name="HashAlgorithm">
/// Which algorithm produced <see cref="ContentHash"/> — null for a fingerprint built before this
/// was tracked (every one today, until P4's <c>SyncState.HashAlgorithm</c> column persists it).
/// Two hashes can only be compared when they came from the same algorithm; see
/// docs/PLAN-CLOUD-PROVIDERS.md P3 and the guard in <c>SyncReconciler</c>.
/// </param>
public sealed record NodeFingerprint(
    string RelativePath,
    bool IsFolder,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? NodeId,
    string? ContentHash,
    RemoteHashAlgorithm? HashAlgorithm = null);
