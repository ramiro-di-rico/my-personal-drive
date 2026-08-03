namespace MyPersonalDrive.Models;

/// <summary>
/// A node returned by the Proton Drive CLI. Field provenance (see docs/PLAN-LOCAL-SYNC.md
/// Appendix A, verified against cli-drive@0.4.2): <see cref="Size"/> and
/// <see cref="ModifiedAt"/> come from a file's `activeRevision.value` (claimedSize /
/// claimedModificationTime — the original local file's size/mtime at upload time, not the
/// encrypted on-server size or the server-side revision timestamp); <see cref="NodeId"/> is the
/// CLI's `uid`, verified stable across rename and move; <see cref="ContentHash"/> is
/// `activeRevision.value.claimedDigests.sha1`, a client-computed hash of the original content.
/// Folders have none of Size/ModifiedAt/ContentHash from activeRevision (they have no
/// revisions); <see cref="ModifiedAt"/> falls back to the top-level `modificationTime` for them.
/// </summary>
public sealed record DriveItem(
    string Path,
    string Name,
    bool IsFolder,
    long? Size = null,
    DateTimeOffset? ModifiedAt = null,
    string? Owner = null,
    bool IsShared = false,
    string? NodeId = null,
    string? ContentHash = null);
