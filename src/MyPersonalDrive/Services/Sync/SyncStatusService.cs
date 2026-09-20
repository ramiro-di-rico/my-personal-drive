using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// One account's answer to "which pair covers this path, and what does it know about it?"
/// (docs/PLAN-SYNC-VISIBILITY.md §2). Holds a <see cref="SyncStatusIndex"/> per pair, built from
/// two whole-pair reads — the same two <see cref="SyncExecutor"/> already does on every cycle —
/// and rebuilt only when something invalidates it.
///
/// Scoped per account, like <see cref="SyncStateStore"/> itself, because a pair id is only
/// meaningful inside one account's store (docs/PLAN-CLOUD-PROVIDERS.md P4). The cross-account
/// view the Properties dialog needs is assembled one level up, by the sync panel, which is the
/// only object that holds every account's store.
/// </summary>
public sealed class SyncStatusService
{
    private readonly SyncStateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, SyncStatusIndex> _indexes = [];
    private readonly Dictionary<int, PathMapper> _mappers = [];
    private IReadOnlyList<SyncPair> _pairs = [];
    private bool _pairsLoaded;

    public SyncStatusService(SyncStateStore store, string accountLabel)
    {
        _store = store;
        AccountLabel = accountLabel;
    }

    /// <summary>The provider/account this service speaks for — what a Properties row is labelled with.</summary>
    public string AccountLabel { get; }

    /// <summary>
    /// Raised after <see cref="InvalidateAsync"/> discards something, so panes can re-ask for the
    /// rows they are already showing instead of re-listing them (docs/PLAN-SYNC-VISIBILITY.md S6).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Drops the cached indexes, so the next lookup reloads. Called when a sync cycle finishes,
    /// when the queue changes and when pairs are added, edited or removed — cheap, because the
    /// reload only happens if something then asks.
    /// </summary>
    public void Invalidate()
    {
        lock (_indexes)
        {
            _indexes.Clear();
            _mappers.Clear();
            _pairsLoaded = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The pair covering a remote path, with that path expressed relative to the pair root — or
    /// null when no pair covers it. Pairs cannot nest (docs/PLAN-LOCAL-SYNC.md §12 makes that a
    /// hard validation error), so at most one pair can match, which is what lets a cloud row show
    /// a single unambiguous status.
    /// </summary>
    public async Task<SyncStatusLookup?> LookupRemoteAsync(string remotePath, CancellationToken ct = default)
    {
        foreach (var pair in await PairsAsync(ct))
        {
            if (!IsUnder(remotePath, pair.RemotePath))
            {
                continue;
            }

            var index = await IndexForAsync(pair, ct);
            return new SyncStatusLookup(index, MapperFor(pair).ToRelativeFromRemote(remotePath), AccountLabel);
        }

        return null;
    }

    /// <summary>
    /// Every pair covering a local path — plural, unlike the remote side: several pairs, even on
    /// different accounts, may be configured against one local folder
    /// (<see cref="SyncPair.SharesLocalFolder"/>, docs/PLAN-LOCAL-SYNC.md §12). This one only sees
    /// its own account's share of them.
    /// </summary>
    public async Task<IReadOnlyList<SyncStatusLookup>> LookupLocalAsync(string localPath, CancellationToken ct = default)
    {
        var results = new List<SyncStatusLookup>();
        foreach (var pair in await PairsAsync(ct))
        {
            if (!IsUnderLocal(localPath, pair.LocalPath))
            {
                continue;
            }

            var index = await IndexForAsync(pair, ct);
            results.Add(new SyncStatusLookup(index, MapperFor(pair).ToRelativeFromLocal(localPath), AccountLabel));
        }

        return results;
    }

    /// <summary>Every pair this account knows, for the "not synced with X either" rows in Properties.</summary>
    public Task<IReadOnlyList<SyncPair>> PairsAsync(CancellationToken ct = default) => LoadPairsAsync(ct);

    private async Task<IReadOnlyList<SyncPair>> LoadPairsAsync(CancellationToken ct)
    {
        lock (_indexes)
        {
            if (_pairsLoaded)
            {
                return _pairs;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            var pairs = await _store.GetPairsAsync(ct);
            lock (_indexes)
            {
                _pairs = pairs;
                _pairsLoaded = true;
            }

            return pairs;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncStatusIndex> IndexForAsync(SyncPair pair, CancellationToken ct)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(pair.Id, out var cached))
            {
                return cached;
            }
        }

        var baseline = await _store.GetBaselineAsync(pair.Id, ct);
        var queue = await _store.GetQueueStatesByPathAsync(pair.Id, ct);
        var index = new SyncStatusIndex(pair, baseline, queue);

        lock (_indexes)
        {
            // Last writer wins: two panes refreshing at once can both build one, and both are
            // built from the same committed rows, so either is correct.
            _indexes[pair.Id] = index;
        }

        return index;
    }

    private PathMapper MapperFor(SyncPair pair)
    {
        lock (_indexes)
        {
            if (_mappers.TryGetValue(pair.Id, out var cached))
            {
                return cached;
            }

            var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
            _mappers[pair.Id] = mapper;
            return mapper;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsUnderLocal(string path, string root)
    {
        var normalizedPath = path.TrimEnd('/', '\\');
        var normalizedRoot = root.TrimEnd('/', '\\');
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>
/// A pair that covers a path, plus that path relative to the pair root and the account label to
/// show it under. The relative path is computed once here because every caller needs it and
/// <see cref="PathMapper"/> throws for a path outside the root — by the time a lookup is returned,
/// that case is already ruled out.
/// </summary>
public sealed record SyncStatusLookup(SyncStatusIndex Index, string RelativePath, string AccountLabel);
