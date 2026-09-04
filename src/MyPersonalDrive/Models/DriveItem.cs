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
/// <param name="IsRemoteOnlyDocument">
/// True for a remote-only node with no binary content the local side could ever sync against —
/// today this means a Google-native file (Docs/Sheets/Slides/Forms/Drawings), which has no
/// checksum at all (docs/PLAN-CLOUD-PROVIDERS.md §8.4/G4). <see cref="Sync.RemoteScanner"/>
/// treats a node with this set the same way it treats an unmappable name: skipped and reported
/// (<see cref="Sync.NodeSkipReason.GoogleNativeFile"/>), never silently dropped or attempted.
/// </param>
public sealed record DriveItem(
    string Path,
    string Name,
    bool IsFolder,
    long? Size = null,
    DateTimeOffset? ModifiedAt = null,
    string? Owner = null,
    bool IsShared = false,
    string? NodeId = null,
    string? ContentHash = null,
    bool IsRemoteOnlyDocument = false);
