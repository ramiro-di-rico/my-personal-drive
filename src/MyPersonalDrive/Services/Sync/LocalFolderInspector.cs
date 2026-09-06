using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The docs/PLAN-LOCAL-SYNC.md §12 pair validations that need to touch the filesystem, kept out of
/// the pure <see cref="SyncPairValidator"/> so that one stays exhaustively testable without IO.
/// </summary>
public static class LocalFolderInspector
{
    /// <summary>
    /// Above this many existing entries, a new pair is worth confirming: for a `LocalToRemote` or
    /// `TwoWay` pair every one of them is an upload, and "Run now" can be pressed without ever
    /// opening the preview that would have shown it.
    /// </summary>
    public const int BusyFolderThreshold = 100;

    /// <summary>
    /// Whether the pair's local folder can actually be synced into: it must exist or be creatable,
    /// and be writable. Returns the message to show, or null when it's usable.
    ///
    /// Worth checking up front rather than discovering it per file: an unwritable folder fails every
    /// single download, which reads as "sync is broken" rather than "that folder is read-only".
    /// </summary>
    public static SyncPairIssue? CheckWritable(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                return new SyncPairIssue(SyncPairIssueKind.LocalPathIsAFile, localPath);
            }

            if (!Directory.Exists(localPath))
            {
                // Creating it is part of the answer to "can we sync here?", and the executor would
                // create it on the first run anyway.
                Directory.CreateDirectory(localPath);
            }

            // Probe rather than inspect permissions: ACLs, mount options and read-only filesystems
            // all produce the same practical answer, and only a write attempt covers them all.
            var probe = Path.Combine(localPath, $".mypersonaldrive-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SyncPairIssue(SyncPairIssueKind.LocalPathNotWritable, localPath, ex.Message);
        }
    }

    /// <summary>
    /// How many entries the folder already holds, counted only up to <paramref name="cap"/> — the
    /// question is "is this a lot?", and enumerating a 200,000-file tree to answer it would freeze
    /// the dialog. Returns null if the folder can't be read, which <see cref="CheckWritable"/>
    /// reports on separately.
    /// </summary>
    public static int? CountEntriesUpTo(string localPath, int cap)
    {
        try
        {
            if (!Directory.Exists(localPath))
            {
                return 0;
            }

            return Directory.EnumerateFileSystemEntries(localPath, "*", SearchOption.AllDirectories).Take(cap).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Free space on the volume holding <paramref name="localPath"/>, or null when it can't be
    /// determined (an unusual filesystem, or the path having gone away).
    /// </summary>
    public static long? AvailableFreeBytes(string localPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(localPath));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// §12's free-space check. Deliberately evaluated against a *plan* rather than at pair-creation
    /// time as §12 suggests: the byte total only exists once the remote side has been scanned, and
    /// scanning it inside the add-pair dialog would cost a full tree walk at ~3.5s per folder
    /// (Appendix A #11a) before the user had even confirmed anything. The preview is where the
    /// numbers are known and where the decision is actually made.
    ///
    /// The 10% headroom is because <c>Bytes</c> comes from the remote listing's claimed sizes, and a
    /// download also needs room for its temp copy before the move into place.
    /// </summary>
    public static SyncPairIssue? CheckFreeSpace(string localPath, long bytesToDownload)
    {
        if (bytesToDownload <= 0)
        {
            return null;
        }

        if (AvailableFreeBytes(localPath) is not { } available)
        {
            return null; // can't tell; don't invent a warning
        }

        var needed = bytesToDownload + bytesToDownload / 10;
        return available >= needed
            ? null
            : new SyncPairIssue(SyncPairIssueKind.NotEnoughFreeSpace, ByteSize.Format(bytesToDownload), ByteSize.Format(available));
    }

}
