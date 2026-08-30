using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.Services.Providers;

/// <summary>Wraps <see cref="LocalFileHasher"/> — the algorithm Proton's CLI reports.</summary>
public sealed class Sha1ContentHasher : IContentHasher
{
    public RemoteHashAlgorithm Algorithm => RemoteHashAlgorithm.Sha1;

    public Task<string> ComputeAsync(string localPath, CancellationToken cancellationToken = default)
        => LocalFileHasher.ComputeSha1Async(localPath, cancellationToken);
}
