using System.Globalization;

namespace MyPersonalDrive.Services;

/// <summary>
/// Human-readable byte counts for the metrics panel. Binary units (KiB steps of 1024) labelled the
/// way the rest of the desktop labels them (KB/MB/GB), which is the convention a user comparing
/// this against their file manager will recognize.
///
/// Formatted with <see cref="CultureInfo.InvariantCulture"/> on purpose: the app's copy is Spanish
/// but its numbers are not localized anywhere else (see <c>DriveNodeViewModel.SizeText</c>), and a
/// culture-dependent decimal separator would make these values differ between a developer's machine
/// and CI for no benefit.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            // Not reachable from CLI-reported sizes, but a negative total would mean an overflow
            // upstream, and printing "-1 B" is a better bug report than throwing inside a binding.
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal below 10 ("1.2 GB"), none above ("512 MB") — the extra digit stops mattering
        // once the leading digits carry the magnitude.
        return value < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0} {Units[unit]}");
    }
}
