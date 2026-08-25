using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Orders a folder listing (docs/PLAN-BROWSER-VIEWS.md V4).
///
/// Folders always come first, whatever the key and whatever the direction. That is not a detail:
/// mixing them into a size ordering would scatter the folders through the list (they have no size
/// of their own — see <see cref="DriveItem"/>), and a "biggest first" listing that opens with a
/// block of 0-byte folders is worse than no sorting.
/// </summary>
public static class DriveItemSorter
{
    public static IReadOnlyList<DriveItem> Sort(IEnumerable<DriveItem> items, DriveSortKey key, bool descending)
    {
        var ordered = items.OrderByDescending(item => item.IsFolder);

        ordered = key switch
        {
            // Nulls last in both directions: a file whose size or timestamp the CLI didn't report
            // is not "the smallest" or "the oldest", it's unknown, and sorting it as if it were a
            // real extreme puts it where the user is most likely to misread it.
            DriveSortKey.Size => descending
                ? ordered.ThenByDescending(item => item.Size ?? long.MinValue)
                : ordered.ThenBy(item => item.Size ?? long.MaxValue),
            DriveSortKey.Modified => descending
                ? ordered.ThenByDescending(item => item.ModifiedAt ?? DateTimeOffset.MinValue)
                : ordered.ThenBy(item => item.ModifiedAt ?? DateTimeOffset.MaxValue),
            DriveSortKey.Kind => descending
                ? ordered.ThenByDescending(item => FileKindClassifier.Classify(item.Name, item.IsFolder).ToString(), StringComparer.Ordinal)
                : ordered.ThenBy(item => FileKindClassifier.Classify(item.Name, item.IsFolder).ToString(), StringComparer.Ordinal),
            _ => descending
                ? ordered.ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Name always breaks the tie, so two listings of the same folder never come back in a
        // different order - every non-name key has many equal values (all folders, every file of
        // one kind), and an unstable order reads as the app losing track of things.
        return ordered
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
