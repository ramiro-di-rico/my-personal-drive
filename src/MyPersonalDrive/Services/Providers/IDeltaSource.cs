using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// One reported change from a delta query: either the item's current state (added or modified),
/// or a marker that it's gone.
/// </summary>
public sealed record DeltaChange(DriveItem Item, bool IsDeleted);

/// <summary>
/// One page (fully drained, including any provider-side pagination) of a delta query.
/// <paramref name="NextToken"/> is opaque — pass it back verbatim to the next
/// <see cref="IDeltaSource.GetChangesAsync"/> call to continue from here; null would mean "start
/// over," which <see cref="IDeltaSource"/> implementations should never actually return (an
/// exhausted/expired token is handled internally — see <see cref="WasFullResync"/>).
/// </summary>
/// <param name="WasFullResync">
/// True when the provider's own cursor had expired and this result is a fresh, full enumeration
/// rather than an incremental diff — the caller should treat every reported item as "confirmed
/// current state," not merge it onto a large gap of history it can no longer reconstruct.
/// </param>
public sealed record DeltaFetchResult(IReadOnlyList<DeltaChange> Changes, string? NextToken, bool WasFullResync);

/// <summary>
/// Optional capability: a provider whose backend can report "what changed since X" instead of
/// requiring a full tree walk every cycle. Present on <see cref="ICloudDriveProvider.DeltaSource"/>
/// only for such providers (OneDrive/Graph) — Proton's CLI has no delta/events command and leaves
/// this null (docs/PLAN-CLOUD-PROVIDERS.md P8).
/// </summary>
public interface IDeltaSource
{
    /// <param name="deltaToken">
    /// The cursor from a previous call's <see cref="DeltaFetchResult.NextToken"/>, or null to
    /// enumerate the entire current tree (first call, or after an expired-token reset).
    /// </param>
    Task<DeltaFetchResult> GetChangesAsync(string? deltaToken, CancellationToken cancellationToken = default);
}
