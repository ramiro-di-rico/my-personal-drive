using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The startup half of docs/PLAN-LOCAL-SYNC.md §7's crash safety. A desktop app gets closed
/// mid-transfer all the time, which leaves two kinds of debris behind: <c>SyncQueue</c> rows
/// stuck in <c>Running</c> (nothing is running — the process that owned them is gone) and
/// half-downloaded files under <c>.mypersonaldrive-tmp</c>. Both are cleaned up here, once,
/// before anything else touches the queue.
/// </summary>
public sealed class SyncCrashRecovery
{
    internal const string TempFolderName = ".mypersonaldrive-tmp";

    private readonly SyncStateStore _stateStore;

    /// <param name="timeProvider">Recovery stamps the rows it revives; tests substitute a fake clock (docs/PLAN-UX-ROUND-4.md Z4).</param>
    public SyncCrashRecovery(SyncStateStore stateStore, TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Returns how many temp directories were cleared, for logging/diagnostics. Never throws
    /// for filesystem reasons: a pair whose local folder is missing or unreadable at startup
    /// (unmounted drive, removed folder) must not stop the app from starting.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.ResetRunningToPendingAsync(cancellationToken);

        var cleared = 0;
        foreach (var pair in await _stateStore.GetPairsAsync(cancellationToken))
        {
            var tempRoot = Path.Combine(pair.LocalPath, TempFolderName);
            try
            {
                if (!Directory.Exists(tempRoot))
                {
                    continue;
                }

                Directory.Delete(tempRoot, recursive: true);
                cleared++;
                await _stateStore.LogAsync(
                    pair.Id, SyncLogLevel.Info, relativePath: null,
                    $"Cleared leftover download temp folder from a previous run: {tempRoot}",
                    _timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _stateStore.LogAsync(
                    pair.Id, SyncLogLevel.Warning, relativePath: null,
                    $"Could not clear the leftover temp folder '{tempRoot}': {ex.Message}",
                    _timeProvider.GetUtcNow(), cancellationToken);
            }
        }

        return cleared;
    }
}
