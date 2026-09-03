namespace MyPersonalDrive.Models;

/// <summary>What the "edit pair" dialog collected — the two settings an existing pair can change without recreating it.</summary>
public sealed record EditSyncPairRequest(SyncDirection Direction, ConflictPolicy ConflictPolicy, bool MirrorDeletes = true);
