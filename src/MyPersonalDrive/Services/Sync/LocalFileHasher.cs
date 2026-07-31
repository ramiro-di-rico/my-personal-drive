using System.Security.Cryptography;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Computes the same hash algorithm the CLI's `activeRevision.value.claimedDigests.sha1`
/// uses (verified in docs/PLAN-LOCAL-SYNC.md Appendix A #14), so a local file's hash is
/// directly comparable to the remote fingerprint's <c>ContentHash</c>. Deliberately not part
/// of <see cref="LocalScanner"/>'s per-scan walk — hashing every file on every scan cycle is
/// wasted work for files whose (size, mtime) already prove they haven't changed; callers
/// should only hash when a cheap comparison is inconclusive or after a transfer completes.
/// </summary>
public static class LocalFileHasher
{
    public static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
