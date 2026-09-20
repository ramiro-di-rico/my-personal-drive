namespace MyPersonalDrive.Models;

/// <summary>
/// One provider's answer about one item, for the Properties dialog's sync section
/// (docs/PLAN-SYNC-VISIBILITY.md S5). A row exists for every signed-in provider, including the ones
/// with no pair covering the item at all: "it is not synced with Google Drive" is exactly what the
/// dialog is being opened to find out, and an absent row does not say it.
/// </summary>
/// <param name="Account">The provider/account label this row speaks for.</param>
/// <param name="Status">Its state, already resolved — the dialog only renders.</param>
/// <param name="StatusText">That state in words, in the current language.</param>
/// <param name="Detail">
/// The pair behind the status — its remote and local folders — or null when no pair covers the
/// item, which is what makes the row read as "not synced with this provider".
/// </param>
/// <param name="LastSyncText">When that pair last completed, or null if it never has.</param>
public sealed record SyncStatusRow(
    string Account,
    SyncNodeStatus Status,
    string StatusText,
    string? Detail,
    string? LastSyncText);
