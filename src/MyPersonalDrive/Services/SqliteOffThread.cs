namespace MyPersonalDrive.Services;

/// <summary>
/// Runs SQLite work somewhere other than the caller's thread.
///
/// <b>Why this has to exist.</b> `Microsoft.Data.Sqlite` has no true asynchronous I/O: its
/// `OpenAsync`/`ExecuteReaderAsync`/`CommitAsync` methods run to completion on the calling thread
/// and hand back an already-finished task. Measured, not assumed — a `SyncItemsAsync` call whose
/// write lock was held elsewhere returned `IsCompleted == true` *after blocking its thread for
/// 30.0 seconds*, then threw `SQLite Error 5: database is locked`.
///
/// That is fatal for a UI, because the store methods are reached from the view models with
/// Avalonia's synchronization context captured, so the "await" resumes on the UI thread and the
/// whole transaction executes there. The browser and the sync engine share one `cache.db`, and WAL
/// allows many readers but only one writer, so any overlap froze the window and disabled every
/// button (each command's `CanExecute` is gated on `!IsLoading`) for up to the busy timeout.
///
/// The hop lives here, inside the stores, rather than at the call sites: a caller cannot know that
/// a method named `…Async` is secretly blocking, so making each of them remember to wrap it is a
/// bug waiting to be reintroduced by the next one added.
/// </summary>
internal static class SqliteOffThread
{
    /// <summary>
    /// How long a statement waits for another connection's write lock before failing. The provider
    /// defaults to 30 seconds, which is the difference between "this felt slow" and "the app hung":
    /// contention here is transient, so failing fast and letting the caller report or retry beats
    /// holding a thread for half a minute. Applied through the connection string.
    /// </summary>
    public const int BusyTimeoutSeconds = 3;

    /// <summary>
    /// Builds a connection string with the bounded timeout above. Takes the raw db path so no caller
    /// has to remember the setting.
    /// </summary>
    public static string ConnectionStringFor(string dbPath)
        => $"Data Source={dbPath};Default Timeout={BusyTimeoutSeconds}";

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool. Because the body never really yields, the
    /// entire operation — open, statements, commit — completes on that pool thread, and its
    /// continuations stay there: <see cref="Task.Run(Func{Task})"/> starts it with no captured
    /// synchronization context, so nothing posts back to the UI thread mid-transaction.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
        => Task.Run(work, cancellationToken);

    public static Task RunAsync(Func<Task> work, CancellationToken cancellationToken = default)
        => Task.Run(work, cancellationToken);
}
