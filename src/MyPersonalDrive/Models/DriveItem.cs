namespace MyPersonalDrive.Models;

public sealed record DriveItem(
    string Path,
    string Name,
    bool IsFolder,
    long? Size = null,
    string? ModifiedAt = null,
    string? Owner = null,
    bool IsShared = false);
