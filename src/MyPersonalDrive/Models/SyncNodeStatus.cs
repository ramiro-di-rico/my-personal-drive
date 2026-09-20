namespace MyPersonalDrive.Models;

/// <summary>
/// What one file or folder's sync situation is, for the badge each browser row draws
/// (docs/PLAN-SYNC-VISIBILITY.md §1.1). Derived entirely from data the app already holds when it
/// draws the row — the <c>SyncState</c> baseline, the <c>SyncQueue</c> rows and the listing
/// itself — never from a fresh remote call or a fresh scan.
///
/// The declaration order is <em>not</em> the precedence order; see
/// <c>Services.Sync.SyncStatusPrecedence</c>. Two things need that order — a folder's rollup over
/// its descendants, and a single path carrying several queue rows — so it is defined once, there,
/// rather than being implied by the numbering here.
/// </summary>
public enum SyncNodeStatus
{
    /// <summary>No sync pair covers this path. The default for everything outside a pair.</summary>
    NotInPair,

    /// <summary>Inside a pair, but an exclusion rule means sync will never touch it.</summary>
    Excluded,

    /// <summary>A baseline exists and this side still matches it.</summary>
    Synced,

    /// <summary>A queued action targets this path and has not started.</summary>
    Pending,

    /// <summary>An action for this path is executing right now.</summary>
    Syncing,

    /// <summary>Changed since the baseline, with nothing queued for it yet.</summary>
    OutOfDate,

    /// <summary>Inside a pair, with no baseline and nothing queued: never transferred.</summary>
    NotSynced,

    /// <summary>A conflict for this path is waiting for the user to decide.</summary>
    Conflict,

    /// <summary>The last action for this path failed.</summary>
    Failed,

    /// <summary>The covering pair is paused or disabled, so nothing will move until it resumes.</summary>
    Paused,

    /// <summary>Folders only — the descendants disagree, so neither "synced" nor "not" is true.</summary>
    PartiallySynced,

    /// <summary>
    /// We genuinely cannot say: a collapsed folder whose children were never listed, or two
    /// fingerprints that aren't comparable (docs/PLAN-CLOUD-PROVIDERS.md P3's algorithm guard).
    /// Rendered as its own neutral glyph — never silently as <see cref="Synced"/>.
    /// </summary>
    Unknown
}
