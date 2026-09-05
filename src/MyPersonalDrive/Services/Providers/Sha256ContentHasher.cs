using System.Security.Cryptography;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Computes a local file's SHA-256 hex digest, so it's directly comparable to Google Drive's own
/// reported <c>sha256Checksum</c> (docs/PLAN-CLOUD-PROVIDERS.md §8.4/G4). A thin wrapper over
/// <see cref="System.Security.Cryptography.SHA256"/> — a standard, trusted algorithm, unlike
/// <c>OneDrive.QuickXorHasher</c>'s from-spec custom implementation, so no golden-vector test is
/// needed here the way P6's live-verification session needed one for QuickXorHash.
/// </summary>
public sealed class Sha256ContentHasher : IContentHasher
{
    public RemoteHashAlgorithm Algorithm => RemoteHashAlgorithm.Sha256;

    public async Task<string> ComputeAsync(string localPath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        // Drive reports sha256Checksum as lowercase hex, not base64 — confirmed against Google's
        // published File resource docs (docs/PLAN-CLOUD-PROVIDERS.md §8.4).
        return Convert.ToHexStringLower(hash);
    }
}
