using System.Text;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// Holds the lines shown in the CLI activity console, bounded so rendering them can never become
/// the most expensive thing the app does.
///
/// <b>Why the length cap exists.</b> The console used to keep 200 lines at their full length and
/// hand the join to a <c>TextBlock</c> with <c>TextWrapping="Wrap"</c>. A single line of
/// `filesystem list --json` is ~1520 characters (node uids alone are 200+ each), so the block held
/// roughly 300 KB of text — and every appended line replaced the string, invalidating measure and
/// making Avalonia re-shape all of it through HarfBuzz on the UI thread. Captured stacks during a
/// hang showed exactly that: <c>TextBlock.MeasureOverride</c> → <c>TextLayout.CreateTextLines</c> →
/// <c>HarfBuzzTextShaper.ShapeText</c>, with the UI thread pegged on a full core for 30 seconds at a
/// time and input dead for the duration.
///
/// So both dimensions are bounded here: the number of lines, and each line's length. A console is
/// for seeing that something is happening and reading errors — neither needs the full JSON payload
/// of a listing, and the truncation marker says plainly that there was more.
/// </summary>
public sealed class CommandLogBuffer
{
    public const int MaxLines = 200;

    /// <summary>
    /// Wide enough for a command line, an error message, or the readable head of a JSON row; far
    /// below the point where shaping cost matters.
    /// </summary>
    public const int MaxLineLength = 300;

    private const string TruncationMarker = "… [truncado]";

    private readonly List<string> _lines = new();
    private readonly int _maxLines;
    private readonly int _maxLineLength;

    public CommandLogBuffer(int maxLines = MaxLines, int maxLineLength = MaxLineLength)
    {
        _maxLines = maxLines;
        _maxLineLength = maxLineLength;
    }

    public int Count => _lines.Count;

    public void Add(string line)
    {
        _lines.Add(Truncate(line));
        if (_lines.Count > _maxLines)
        {
            _lines.RemoveRange(0, _lines.Count - _maxLines);
        }
    }

    public void AddRange(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Add(line);
        }
    }

    public void Clear() => _lines.Clear();

    /// <summary>The console's text, newest lines last. Built once per flush, never once per line.</summary>
    public string Render() => string.Join(Environment.NewLine, _lines);

    /// <summary>The lines as saved to a file, which is the one place the full text would be wanted —
    /// and is exactly why the caller should be told these are already truncated.</summary>
    public IReadOnlyList<string> Lines => _lines;

    private string Truncate(string line)
    {
        if (line.Length <= _maxLineLength)
        {
            return line;
        }

        var builder = new StringBuilder(_maxLineLength + TruncationMarker.Length);
        builder.Append(line, 0, _maxLineLength);
        builder.Append(TruncationMarker);
        return builder.ToString();
    }
}
