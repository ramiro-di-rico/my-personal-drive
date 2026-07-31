namespace MyPersonalDrive.Models;

/// <summary>
/// A path the reconciler flagged as divergent. For <see cref="ConflictReason.RemoteDeletedLocalChanged"/>
/// and <see cref="ConflictReason.LocalDeletedRemoteChanged"/> this is informational — the plan
/// already contains the auto-resolved action (re-upload/re-download); for
/// <see cref="ConflictReason.BothChanged"/> and <see cref="ConflictReason.BothAppearedDiffering"/>
/// under <see cref="ConflictPolicy.Ask"/>, no action exists yet and the UI must resolve it.
/// </summary>
public sealed record SyncConflict(string RelativePath, ConflictReason Reason);
