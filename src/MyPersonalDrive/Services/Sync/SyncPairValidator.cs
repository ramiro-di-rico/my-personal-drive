using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The checks from docs/PLAN-LOCAL-SYNC.md §12 that need no IO, so they can be tested exhaustively:
/// path shape, the refuse-to-sync-your-whole-home rule, and overlap against pairs that already
/// exist. Returns the typed reason to show the user, or null when the pair is acceptable — the
/// wording lives in <c>ViewModels.SyncIssuePresenter</c>, not here (docs/PLAN-I18N.md §9).
/// </summary>
public static class SyncPairValidator
{
    /// <param name="sameAccountPairs">This account's own pairs — the only ones a remote-path overlap is checked against (two different providers' remote trees are unrelated, so an identical-looking remote path string means nothing across accounts).</param>
    /// <param name="allAccountPairs">
    /// Every account's pairs, for the local-path overlap check — defaults to <paramref name="sameAccountPairs"/> for a single-account caller. A local folder is a real filesystem path shared by whichever provider looks at it, so overlap there has to be checked account-wide, not just within one account. See <see cref="FindOverlap"/> for the upload-only exception to that.
    /// </param>
    /// <param name="mirrorDeletes">
    /// The pair's own mirror-deletes setting. Only consulted for the shared-folder rule: a download
    /// pair that mirrors deletes cannot share a folder, because everything the other provider put
    /// there is missing from its source and would be trashed. See <see cref="FindOverlap"/>.
    /// </param>
    public static SyncPairIssue? Validate(string remotePath, string localPath, SyncDirection direction, IReadOnlyList<SyncPair> sameAccountPairs, IReadOnlyList<SyncPair>? allAccountPairs = null, bool mirrorDeletes = true)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
        {
            return new SyncPairIssue(SyncPairIssueKind.RemotePathNotAbsolute);
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return new SyncPairIssue(SyncPairIssueKind.LocalPathMissing);
        }

        var trimmedLocal = localPath.TrimEnd('/', '\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmedLocal.Length == 0 || trimmedLocal == "/" || string.Equals(trimmedLocal, home.TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            return new SyncPairIssue(SyncPairIssueKind.LocalPathIsHomeOrRoot);
        }

        return FindOverlap(remotePath, localPath, direction, mirrorDeletes, sameAccountPairs, allAccountPairs ?? sameAccountPairs);
    }

    /// <summary>
    /// The shared-folder rule re-run for an edit. Remote and local paths never change on edit (see
    /// <c>SyncStateStore.UpdatePairSettingsAsync</c>'s own doc comment), so nothing else
    /// <see cref="Validate"/> checks can newly break — but both settings this does receive can turn
    /// a safe shared folder unsafe:
    ///
    /// <list type="bullet">
    /// <item>switching to <see cref="SyncDirection.TwoWay"/> makes the pair authoritative over a
    /// folder it does not own alone;</item>
    /// <item>switching a download pair back to mirroring deletes points it at the other provider's
    /// files, all of which are missing from its own remote.</item>
    /// </list>
    ///
    /// Only pairs sharing this pair's <em>exact</em> folder are considered: a nested overlap could
    /// never have been created in the first place, so encountering one here would mean it predates
    /// the rule, and re-validating an edit is not the moment to start refusing it.
    /// </summary>
    public static SyncPairIssue? ValidateEdit(SyncPair pair, SyncDirection newDirection, bool newMirrorDeletes, IReadOnlyList<SyncPair> allAccountPairs)
    {
        if (newDirection == pair.Direction && newMirrorDeletes == pair.MirrorDeletes)
        {
            // Re-saving the settings it already has can't newly break anything — even when those
            // settings predate this rule and wouldn't pass it fresh. Refusing to save an unrelated
            // change (the conflict policy) on such a pair would strand it.
            return null;
        }

        var thisLocal = NormalizeLocal(pair.LocalPath);
        foreach (var other in allAccountPairs)
        {
            if (other.Id == pair.Id)
            {
                continue;
            }

            if (!string.Equals(thisLocal, NormalizeLocal(other.LocalPath), StringComparison.Ordinal))
            {
                continue;
            }

            var issue = CheckSharable(newDirection, newMirrorDeletes, other);
            if (issue is not null)
            {
                return issue;
            }
        }

        return null;
    }

    /// <summary>
    /// Rejects a pair whose local or remote scope overlaps an existing one.
    ///
    /// <b>Overlapping local folders are actively destructive</b>, not merely redundant — normally.
    /// Take <c>~/A ↔ /my-files/X</c> and <c>~/A/Sub ↔ /my-files/Y</c>: the first pair's scanner
    /// walks <c>~/A/Sub</c> too, its own remote root has no <c>Sub</c>, so it concludes the folder
    /// was deleted remotely and moves it to the local trash — which the second pair then downloads
    /// again, forever. <b>The one shape that's actually safe</b> is every overlapping pair being
    /// <see cref="SyncDirection.LocalToRemote"/>: a pair that never writes to its local folder can't
    /// delete or overwrite what another upload-only pair put there either, which is exactly what
    /// makes "upload this one folder to several providers" a legitimate, supported configuration —
    /// checked across every account (<paramref name="allAccountPairs"/>), not just this one, since
    /// the local folder is the same physical filesystem path regardless of which provider owns the
    /// pair.
    ///
    /// <b>Overlapping remote folders break echo suppression</b>, which is keyed per pair
    /// (<see cref="SyncEchoSuppressor"/>). Pair A has no idea pair B just trashed something, so it
    /// sees the node still listed (Appendix A #15's stale listing), reads it as "new remotely", and
    /// downloads back what the other pair deleted — exactly the resurrection bug that fix removed,
    /// reintroduced across pairs. Unlike the local check, this only makes sense within one account
    /// (<paramref name="sameAccountPairs"/>): two different providers' remote trees are unrelated
    /// storage systems, so an identical-looking remote path string across them means nothing.
    /// </summary>
    private static SyncPairIssue? FindOverlap(string remotePath, string localPath, SyncDirection direction, bool mirrorDeletes, IReadOnlyList<SyncPair> sameAccountPairs, IReadOnlyList<SyncPair> allAccountPairs)
    {
        var newLocal = NormalizeLocal(localPath);
        var newRemote = NormalizeRemote(remotePath);

        foreach (var pair in allAccountPairs)
        {
            var existingLocal = NormalizeLocal(pair.LocalPath);
            if (!Overlaps(newLocal, existingLocal, Path.DirectorySeparatorChar))
            {
                continue;
            }

            if (!string.Equals(newLocal, existingLocal, StringComparison.Ordinal))
            {
                // Nesting, not sharing. Only two upload-only pairs survive it, and for a different
                // reason than the exact-match case below: neither writes locally at all, so the
                // outer one merely uploads the inner one's folder too — redundant, not destructive.
                // Anything that *does* write locally hits the original problem, which per-file
                // protection cannot fix: the outer pair's scanner walks the inner pair's folder and
                // reads content its own remote never had as content deleted from it.
                if (direction == SyncDirection.LocalToRemote && pair.Direction == SyncDirection.LocalToRemote)
                {
                    continue;
                }

                return new SyncPairIssue(SyncPairIssueKind.LocalOverlaps, pair.LocalPath, pair.RemotePath);
            }

            var issue = CheckSharable(direction, mirrorDeletes, pair);
            if (issue is not null)
            {
                return issue;
            }
        }

        foreach (var pair in sameAccountPairs)
        {
            var existingRemote = NormalizeRemote(pair.RemotePath);
            if (Overlaps(newRemote, existingRemote, '/'))
            {
                return string.Equals(newRemote, existingRemote, StringComparison.Ordinal)
                    ? new SyncPairIssue(SyncPairIssueKind.RemoteAlreadySynced, pair.RemotePath, pair.LocalPath)
                    : new SyncPairIssue(SyncPairIssueKind.RemoteOverlaps, pair.RemotePath, pair.LocalPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether two pairs may share one local folder, and if not, why. Applied to each side
    /// independently, so a shared folder is only ever made of pairs that each pass on their own.
    ///
    /// Two conditions, each closing a distinct way of destroying data
    /// (docs/PLAN-LOCAL-SYNC.md §12):
    ///
    /// <list type="number">
    /// <item><b>No two-way pair.</b> Its local side is authoritative too, so it would upload the
    /// other provider's files into its own remote as if they were the user's, and answer their
    /// later disappearance with a remote trash. There is no per-file protection that fixes this:
    /// the pair genuinely cannot tell the difference.</item>
    /// <item><b>Two download pairs must both be additive.</b> Everything the other provider put in
    /// the folder is missing from this pair's remote, and a mirroring pair deletes exactly that —
    /// the whole other provider's contents, on the first run.</item>
    /// </list>
    ///
    /// The second check is deliberately conditioned on there being <em>two</em> writers. An
    /// upload-only pair never writes locally, so it can neither delete nor overwrite anything in
    /// the folder — which is why any number of upload pairs may share one (the case supported
    /// before sharing was generalised), why one of them may sit next to a download pair
    /// ("fetch from here, replicate to those"), and why that download pair may still mirror
    /// strictly: with nothing else writing there, its mirror only ever governs the user's own
    /// files, exactly as it did when it owned the folder alone.
    ///
    /// What this does <em>not</em> prevent is two pairs disagreeing about a same-named file; that
    /// is left to <see cref="SyncReconciler"/>, which raises
    /// <see cref="ConflictReason.ForeignDestinationFile"/> rather than overwriting, and is why a
    /// shared pair keeps a baseline (<see cref="SyncPair.SharesLocalFolder"/>).
    /// </summary>
    private static SyncPairIssue? CheckSharable(SyncDirection direction, bool mirrorDeletes, SyncPair existing)
    {
        if (direction == SyncDirection.TwoWay)
        {
            return new SyncPairIssue(SyncPairIssueKind.SharedFolderNeedsOneWay, existing.LocalPath, existing.RemotePath);
        }

        if (existing.Direction == SyncDirection.TwoWay)
        {
            return new SyncPairIssue(SyncPairIssueKind.SharedFolderNeedsOneWay, existing.LocalPath, existing.RemotePath);
        }

        // Only download pairs write into the folder, so the additive requirement applies exactly
        // when there are two of them. A download pair next to an upload-only one is still mirroring
        // a folder nothing else writes to — its strict mirror endangers only the user's own files,
        // which is what a mirror is for and was already true before sharing existed.
        if (direction != SyncDirection.RemoteToLocal || existing.Direction != SyncDirection.RemoteToLocal)
        {
            return null;
        }

        return mirrorDeletes || existing.MirrorDeletes
            ? new SyncPairIssue(SyncPairIssueKind.SharedFolderNeedsAdditive, existing.LocalPath, existing.RemotePath)
            : null;
    }

    /// <summary>
    /// Same path, or one inside the other. The separator check is what keeps <c>/a/bc</c> from
    /// counting as nested inside <c>/a/b</c>.
    /// </summary>
    private static bool Overlaps(string first, string second, char separator)
        => string.Equals(first, second, StringComparison.Ordinal)
           || first.StartsWith(second + separator, StringComparison.Ordinal)
           || second.StartsWith(first + separator, StringComparison.Ordinal);

    /// <summary>
    /// Exposed so callers deciding whether two pairs share a folder use this exact definition rather
    /// than a second one — <c>ViewModels.Sync.SyncPanelViewModel</c> maintains
    /// <see cref="SyncPair.SharesLocalFolder"/> from it, and a looser or stricter comparison there
    /// would mark the wrong pairs and either skip the protection or apply it needlessly.
    ///
    /// Resolves <c>.</c>, <c>..</c> and redundant separators so two spellings of one folder compare
    /// equal. Ordinal (case-sensitive) because Linux is; on a case-insensitive filesystem two
    /// differently-cased spellings of the same folder would slip through, which is a gap worth
    /// noting if this app ever ships for Windows or macOS.
    /// </summary>
    public static string NormalizeLocal(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('/', '\\');
        }
    }

    private static string NormalizeRemote(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
