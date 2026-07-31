namespace MyPersonalDrive.Models;

/// <summary>A durable <c>SyncQueue</c> row — a <see cref="SyncAction"/> plus execution bookkeeping.</summary>
public sealed record QueuedSyncAction(
    long Id,
    int PairId,
    string RelativePath,
    SyncOperation Operation,
    string? SecondaryPath,
    long? Bytes,
    int Priority,
    int AttemptCount,
    SyncQueueState State,
    string? LastError,
    DateTimeOffset EnqueuedAt);
