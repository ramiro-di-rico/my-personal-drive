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
}
