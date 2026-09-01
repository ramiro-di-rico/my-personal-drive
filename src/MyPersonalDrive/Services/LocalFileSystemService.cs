using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.Services;

/// <summary>
/// The only place that touches the local filesystem for the explorer's local pane (docs/
/// INTERFACE_IMPROVEMENT_PLAN.md Task 3) — kept out of the ViewModel layer, which never touches
/// the filesystem directly (.claude/skills/add-feature/SKILL.md). Read-only: browsing only, no
/// create/rename/delete (those are separate plan tasks).
/// </summary>
public class LocalFileSystemService
{
    /// <summary>
    /// One directory's entries as <see cref="DriveItem"/> — the same record the cloud side uses,
    /// since its shape (Path/Name/IsFolder/Size/ModifiedAt) is provider-agnostic; the cloud-only
    /// fields (Owner/IsShared/NodeId/ContentHash) are simply left at their defaults here.
    ///
    /// An entry this process can't stat (permission denied mid-enumeration, a broken symlink) is
    /// skipped rather than failing the whole listing — one bad entry shouldn't hide the rest of an
    /// otherwise-readable folder. Failing to enumerate the directory itself (it doesn't exist, or
    /// isn't readable at all) is left to the caller to catch.
    /// </summary>
    public virtual IReadOnlyList<DriveItem> ListDirectory(string path, bool includeHidden)
    {
        var directory = new DirectoryInfo(path);
        var items = new List<DriveItem>();

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            try
            {
                if (!includeHidden && IsHidden(entry))
                {
                    continue;
                }

                var isFolder = entry is DirectoryInfo;
                items.Add(new DriveItem(
                    Path: entry.FullName,
                    Name: entry.Name,
                    IsFolder: isFolder,
                    Size: isFolder ? null : ((FileInfo)entry).Length,
                    ModifiedAt: entry.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip: e.g. a symlink target that vanished between enumeration and stat.
            }
        }

        return items;
    }

    private static bool IsHidden(FileSystemInfo entry)
        => entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden);

    public virtual string GetHomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Delegates to the sync domain's own free-space probe rather than duplicating it.</summary>
    public virtual long? AvailableFreeBytes(string path) => LocalFolderInspector.AvailableFreeBytes(path);

    /// <summary>
    /// Whether a file or folder already sits at <paramref name="path"/> — used by drag-and-drop
    /// downloads (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5) to detect a naming conflict before
    /// asking the CLI to download over it.
    /// </summary>
    public virtual bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Permanently deletes a local file or folder (recursively for a folder) — docs/
    /// INTERFACE_IMPROVEMENT_PLAN.md Task 6's local-pane context menu. There is no local "trash":
    /// unlike the cloud side, the OS provides no CLI-reachable recycle bin here, so the caller must
    /// confirm with the user before calling this.
    /// </summary>
    public virtual void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Renames a local file or folder in place, returning its new full path.</summary>
    public virtual string Rename(string path, string newName)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new IOException($"'{path}' has no parent directory.");
        var newPath = Path.Combine(parent, newName);

        if (Directory.Exists(path))
        {
            Directory.Move(path, newPath);
        }
        else
        {
            File.Move(path, newPath);
        }

        return newPath;
    }
}
