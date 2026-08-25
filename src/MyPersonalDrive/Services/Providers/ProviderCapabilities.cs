namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// What a provider's backend actually supports, so callers stop assuming Proton-CLI-shaped
/// behavior is universal. See docs/PLAN-CLOUD-PROVIDERS.md §2.5.
/// </summary>
public enum RemoteHashAlgorithm
{
    None,
    Sha1,
    Sha256,
    QuickXor
}

public sealed record ProviderCapabilities(
    RemoteHashAlgorithm RemoteHash,
    bool SupportsServerSideMove,
    bool SupportsServerSideCopy,
    bool CopyIsAsynchronous,
    bool SupportsBatchMove,
    bool SupportsDelta,
    bool RequiresRemoteViewInvalidation,
    long? MaxSingleShotUploadBytes,
    long? UploadChunkSizeBytes,
    int MaxRecommendedConcurrency,
    bool CanSetRemoteModificationTime);
