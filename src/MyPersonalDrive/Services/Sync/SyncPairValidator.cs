using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The checks from docs/PLAN-LOCAL-SYNC.md §12 that need no IO, so they can be tested exhaustively:
/// path shape, the refuse-to-sync-your-whole-home rule, and overlap against pairs that already
/// exist. Returns the message to show the user, or null when the pair is acceptable.
/// </summary>
public static class SyncPairValidator
{
    /// <param name="sameAccountPairs">This account's own pairs — the only ones a remote-path overlap is checked against (two different providers' remote trees are unrelated, so an identical-looking remote path string means nothing across accounts).</param>
    /// <param name="allAccountPairs">
    /// Every account's pairs, for the local-path overlap check — defaults to <paramref name="sameAccountPairs"/> for a single-account caller. A local folder is a real filesystem path shared by whichever provider looks at it, so overlap there has to be checked account-wide, not just within one account. See <see cref="FindOverlap"/> for the upload-only exception to that.
    /// </param>
    public static string? Validate(string remotePath, string localPath, SyncDirection direction, IReadOnlyList<SyncPair> sameAccountPairs, IReadOnlyList<SyncPair>? allAccountPairs = null)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
        {
            return "La ruta remota tiene que ser una ruta absoluta que empiece con '/'.";
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return "Elegí una carpeta local.";
        }

        var trimmedLocal = localPath.TrimEnd('/', '\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmedLocal.Length == 0 || trimmedLocal == "/" || string.Equals(trimmedLocal, home.TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            return "No se sincroniza tu carpeta personal entera ni la raíz del sistema de archivos — elegí una subcarpeta específica.";
        }

        return FindOverlap(remotePath, localPath, direction, sameAccountPairs, allAccountPairs ?? sameAccountPairs);
    }

    /// <summary>
    /// The same overlap rule <see cref="FindOverlap"/> enforces at creation time, re-run for a pair
    /// whose <em>direction</em> is about to change (remote/local paths never change on edit — see
    /// <c>SyncStateStore.UpdatePairSettingsAsync</c>'s own doc comment — so nothing else <see
    /// cref="Validate"/> checks can newly break). Switching a pair's own direction can only be
    /// unsafe when it starts writing to a local folder another account already shares as
    /// upload-only; every other pair sharing that folder was already guaranteed
    /// <see cref="SyncDirection.LocalToRemote"/> by that same rule, so any surviving overlap here is
    /// exactly the case to block.
    /// </summary>
    public static string? ValidateDirectionChange(SyncPair pair, SyncDirection newDirection, IReadOnlyList<SyncPair> allAccountPairs)
    {
        if (newDirection == pair.Direction || newDirection == SyncDirection.LocalToRemote)
        {
            // Re-saving the direction it already has can't newly break anything (even if that
            // direction predates this rule and wouldn't pass it fresh); moving *to* upload-only can
            // only make an existing overlap safer.
            return null;
        }

        var thisLocal = NormalizeLocal(pair.LocalPath);
        foreach (var other in allAccountPairs)
        {
            if (other.Id == pair.Id)
            {
                continue;
            }

            var otherLocal = NormalizeLocal(other.LocalPath);
            if (Overlaps(thisLocal, otherLocal, Path.DirectorySeparatorChar))
            {
                return $"'{pair.LocalPath}' también está sincronizada (solo subida) con '{other.RemotePath}'. " +
                       "Cambiar este par a una dirección que escribe en la carpeta local podría borrar o " +
                       "sobrescribir lo que ese par sube. Eliminá o cambiá el otro par primero.";
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
    private static string? FindOverlap(string remotePath, string localPath, SyncDirection direction, IReadOnlyList<SyncPair> sameAccountPairs, IReadOnlyList<SyncPair> allAccountPairs)
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

            if (direction == SyncDirection.LocalToRemote && pair.Direction == SyncDirection.LocalToRemote)
            {
                continue; // both upload-only — fan-out to another provider, not a conflict
            }

            return string.Equals(newLocal, existingLocal, StringComparison.Ordinal)
                ? $"'{pair.LocalPath}' ya está sincronizada con '{pair.RemotePath}'."
                : $"Esa carpeta local se superpone con '{pair.LocalPath}', que ya está sincronizada con " +
                  $"'{pair.RemotePath}'. Dos pares compartiendo una carpeta tratarían los archivos del otro como " +
                  "eliminaciones. Elegí una carpeta fuera de ella.";
        }

        foreach (var pair in sameAccountPairs)
        {
            var existingRemote = NormalizeRemote(pair.RemotePath);
            if (Overlaps(newRemote, existingRemote, '/'))
            {
                return string.Equals(newRemote, existingRemote, StringComparison.Ordinal)
                    ? $"'{pair.RemotePath}' ya está sincronizada con '{pair.LocalPath}'."
                    : $"Esa carpeta remota se superpone con '{pair.RemotePath}', que ya está sincronizada con " +
                      $"'{pair.LocalPath}'. Dos pares cubriendo el mismo subárbol remoto pueden deshacer las " +
                      "eliminaciones del otro. Elegí una carpeta fuera de ella.";
            }
        }

        return null;
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
    /// Resolves <c>.</c>, <c>..</c> and redundant separators so two spellings of one folder compare
    /// equal. Ordinal (case-sensitive) because Linux is; on a case-insensitive filesystem two
    /// differently-cased spellings of the same folder would slip through, which is a gap worth
    /// noting if this app ever ships for Windows or macOS.
    /// </summary>
    private static string NormalizeLocal(string path)
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
