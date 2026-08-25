using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Computes a local file's content hash using whichever algorithm the active provider's remote
/// side reports, so the two are directly comparable (docs/PLAN-CLOUD-PROVIDERS.md P3). Chosen
/// from <see cref="ProviderCapabilities.RemoteHash"/> — <see cref="Sha1ContentHasher"/> today,
/// a <c>QuickXorContentHasher</c> once P6 needs one.
/// </summary>
public interface IContentHasher
{
    RemoteHashAlgorithm Algorithm { get; }

    Task<string> ComputeAsync(string localPath, CancellationToken cancellationToken = default);
}
