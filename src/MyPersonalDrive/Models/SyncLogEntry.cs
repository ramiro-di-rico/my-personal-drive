namespace MyPersonalDrive.Models;

public sealed record SyncLogEntry(
    long Id,
    int? PairId,
    DateTimeOffset Timestamp,
    SyncLogLevel Level,
    string? RelativePath,
    string Message);
