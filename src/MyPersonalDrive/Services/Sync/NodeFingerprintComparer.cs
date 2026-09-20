using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// "Unchanged" per docs/PLAN-LOCAL-SYNC.md §5.4: hash+size equality when both sides have a hash
/// (exact, no tolerance needed); otherwise size equality plus mtime within a tolerance.
///
/// Extracted from <see cref="SyncReconciler"/> — which still delegates here — when
/// <see cref="SyncStatusIndex"/> needed the same predicate to decide whether a listed node still
/// matches its baseline (docs/PLAN-SYNC-VISIBILITY.md §1.2). A second copy of this comparison is
/// the one thing that would make a badge disagree with the engine that drew it.
/// </summary>
public static class NodeFingerprintComparer
{
    /// <summary>The mtime slack two sides are allowed before they count as different.</summary>
    public static readonly TimeSpan DefaultMtimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>A missing fingerprint on either side counts as changed — there's nothing to compare.</summary>
    public static bool IsChanged(NodeFingerprint? current, NodeFingerprint? baseline, TimeSpan tolerance)
        => !AreEquivalent(current, baseline, tolerance);

    /// <summary>
    /// Precondition on the hash branch below: <see cref="NodeFingerprint.ContentHash"/> strings
    /// are only comparable when both sides' <see cref="NodeFingerprint.HashAlgorithm"/> are the
    /// same algorithm. A provider whose remote hash isn't SHA-1 (docs/PLAN-CLOUD-PROVIDERS.md P6)
    /// would otherwise have a locally-computed SHA-1 compared against a QuickXor digest — two
    /// unrelated strings that will simply never match, silently reporting every file as changed.
    /// <see cref="RemoteHashAlgorithm.None"/> or a missing tag is not treated as a mismatch: it
    /// means "unknown", the honest state of every fingerprint before P4 persists the algorithm,
    /// and today's only algorithm (Sha1) is correct on both sides regardless.
    /// </summary>
    public static bool AreEquivalent(NodeFingerprint? a, NodeFingerprint? b, TimeSpan tolerance)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Size != b.Size)
        {
            return false;
        }

        if (a.ContentHash is not null && b.ContentHash is not null && !IsAlgorithmMismatch(a.HashAlgorithm, b.HashAlgorithm))
        {
            return string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal);
        }

        if (a.ModifiedAt is null || b.ModifiedAt is null)
        {
            return a.ModifiedAt == b.ModifiedAt;
        }

        return (a.ModifiedAt.Value - b.ModifiedAt.Value).Duration() <= tolerance;
    }

    /// <summary>
    /// True only when both sides name a <em>known</em> algorithm and they differ. Matches the doc
    /// comment on <see cref="AreEquivalent"/>: <see cref="RemoteHashAlgorithm.None"/> is "no
    /// algorithm", exactly as meaningless for this comparison as a missing (null) tag, so neither
    /// one — alone or together — counts as a mismatch against anything.
    /// </summary>
    public static bool IsAlgorithmMismatch(RemoteHashAlgorithm? a, RemoteHashAlgorithm? b)
        => IsKnownAlgorithm(a) && IsKnownAlgorithm(b) && a != b;

    private static bool IsKnownAlgorithm(RemoteHashAlgorithm? algorithm)
        => algorithm is not (null or RemoteHashAlgorithm.None);
}
