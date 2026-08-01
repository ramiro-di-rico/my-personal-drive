using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Writes the three-way baseline (<c>SyncState</c>) after an action succeeds, per
/// docs/PLAN-LOCAL-SYNC.md §7: "only after confirmed success, and by re-reading the real
/// fingerprint of both sides." Re-reading matters because neither side's post-transfer state is
/// predictable from the plan — an upload mints a new remote revision with a server-assigned
/// `uid`/hash, and a download's local mtime is whatever the executor just set.
///
/// One instance per sync run. It caches remote listings per parent folder, because a fresh
/// remote read costs ~3.5s of process startup (Appendix A #11a) and a run that uploads 40 files
/// into one folder must not pay that 40 times. <see cref="InvalidateRemoteFolder"/> drops the
/// cache entry for a folder this run has just written to, so the next baseline write re-lists it.
/// </summary>
public sealed class SyncBaselineWriter
{
    private readonly ProtonDriveService _protonDriveService;
    private readonly SyncStateStore _stateStore;
    private readonly PathMapper _mapper;
    private readonly int _pairId;
    private readonly Dictionary<string, Dictionary<string, DriveItem>> _remoteFolderCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, NodeFingerprint>> _seededRemote = new(StringComparer.Ordinal);

    public SyncBaselineWriter(ProtonDriveService protonDriveService, SyncStateStore stateStore, PathMapper mapper, int pairId)
    {
        _protonDriveService = protonDriveService;
        _stateStore = stateStore;
        _mapper = mapper;
        _pairId = pairId;
    }

    /// <summary>
    /// Seeds the cache from the run's initial remote scan, so paths this run never wrote to are
    /// recorded without a single extra CLI call.
    /// </summary>
    public void SeedFromScan(IReadOnlyDictionary<string, NodeFingerprint> remote)
    {
        foreach (var (relativePath, fingerprint) in remote)
        {
            var (parent, name) = SplitRelative(relativePath);
            if (!_seededRemote.TryGetValue(parent, out var bucket))
            {
                bucket = new Dictionary<string, NodeFingerprint>(StringComparer.Ordinal);
                _seededRemote[parent] = bucket;
            }

            bucket[name] = fingerprint;
        }
    }

    /// <summary>Call after any operation that changed a remote folder's contents.</summary>
    public void InvalidateRemoteFolder(string relativeFolderPath)
    {
        _remoteFolderCache.Remove(_mapper.ToRemoteAbsolute(relativeFolderPath));
        _seededRemote.Remove(relativeFolderPath);
    }

    /// <summary>
    /// Records the current state of both sides for <paramref name="relativePath"/>. A side that
    /// no longer exists is stored as null, which is exactly what the decision table needs to see
    /// next run (a null baseline side is "changed" against any present fingerprint).
    /// </summary>
    public async Task RecordAsync(string relativePath, bool isFolder, DateTimeOffset syncedAt, CancellationToken cancellationToken)
    {
        var local = ReadLocalFingerprint(relativePath, isFolder);
        if (local is not null && !isFolder)
        {
            local = local with { ContentHash = await TryHashAsync(relativePath, cancellationToken) };
        }

        var remote = await ReadRemoteFingerprintAsync(relativePath, cancellationToken);
        var entry = new SyncBaselineEntry(relativePath, isFolder, local, remote);
        await _stateStore.UpsertBaselineAsync(_pairId, entry, syncedAt, cancellationToken);
    }

    public Task ClearAsync(string relativePath, CancellationToken cancellationToken)
        => _stateStore.RemoveBaselineAsync(_pairId, relativePath, cancellationToken);

    private NodeFingerprint? ReadLocalFingerprint(string relativePath, bool isFolder)
    {
        var absolutePath = _mapper.ToLocalAbsolute(relativePath);
        try
        {
            if (isFolder)
            {
                return Directory.Exists(absolutePath)
                    ? new NodeFingerprint(relativePath, true, null, new DateTimeOffset(Directory.GetLastWriteTimeUtc(absolutePath), TimeSpan.Zero), null, null)
                    : null;
            }

            var info = new FileInfo(absolutePath);
            return info.Exists
                ? new NodeFingerprint(relativePath, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), null, null)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable local side is recorded as absent rather than crashing the run: the
            // next scan will see the real state, and a null baseline side is safe (it reads as
            // "changed", so nothing gets silently skipped).
            return null;
        }
    }

    private async Task<string?> TryHashAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return await LocalFileHasher.ComputeSha1Async(_mapper.ToLocalAbsolute(relativePath), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // size+mtime comparison still applies (§5.4's fallback)
        }
    }

    private async Task<NodeFingerprint?> ReadRemoteFingerprintAsync(string relativePath, CancellationToken cancellationToken)
    {
        var (parent, name) = SplitRelative(relativePath);

        if (_seededRemote.TryGetValue(parent, out var seededBucket) && seededBucket.TryGetValue(name, out var seeded))
        {
            return seeded;
        }

        var parentRemoteAbsolute = _mapper.ToRemoteAbsolute(parent);
        if (!_remoteFolderCache.TryGetValue(parentRemoteAbsolute, out var bucket))
        {
            bucket = new Dictionary<string, DriveItem>(StringComparer.Ordinal);
            try
            {
                foreach (var item in await _protonDriveService.LoadFolderAsync(parentRemoteAbsolute, cancellationToken))
                {
                    bucket[item.Name] = item;
                }
            }
            catch (CliException)
            {
                // The parent folder is gone or unreadable — treat the remote side as absent
                // rather than failing an action that already succeeded.
            }

            _remoteFolderCache[parentRemoteAbsolute] = bucket;
        }

        return bucket.TryGetValue(name, out var driveItem)
            ? new NodeFingerprint(relativePath, driveItem.IsFolder, driveItem.Size, driveItem.ModifiedAt, driveItem.NodeId, driveItem.ContentHash)
            : null;
    }

    private static (string Parent, string Name) SplitRelative(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0
            ? (string.Empty, relativePath)
            : (relativePath[..lastSlash], relativePath[(lastSlash + 1)..]);
    }
}
