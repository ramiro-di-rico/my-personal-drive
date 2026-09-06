using System.IO;
using System.Text;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services;

/// <summary>
/// Loads the contents of a remote plain-text file for the in-app viewer. Kept behind an interface
/// so view-model tests can hand back canned text without a CLI or a filesystem.
/// </summary>
public interface ITextFilePreviewLoader
{
    Task<TextFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real loader: the CLI has no "read a file" command, only <c>filesystem download</c>, so a
/// preview is a download into a private temp folder, a bounded read, and a delete. Nothing is left
/// under the user's own folders — a preview is not a download, and must not look like one on disk.
///
/// This is a service and not view-model code precisely because it touches the filesystem
/// (AGENTS.md: view models never do).
/// </summary>
public sealed class TextFilePreviewService : ITextFilePreviewLoader
{
    private readonly IDriveOperations _operations;
    private readonly string _tempRoot;

    public TextFilePreviewService(IDriveOperations operations, string? tempRoot = null)
    {
        _operations = operations;
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "MyPersonalDrive", "preview");
    }

    public async Task<TextFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder)
        {
            throw new LocalizedInvalidOperationException(
                "Folders have no text to preview.",
                LocalizedText.Of(StringKeys.Error.PreviewFolderHasNoText));
        }

        var directory = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await _operations.DownloadFileAsync(item.Path, directory, cancellationToken);

            // The CLI names the download after the remote node, but don't rely on that being
            // byte-identical: the folder is ours and holds exactly one file, so if the expected
            // name isn't there, whatever landed is the file.
            var downloadedPath = Path.Combine(directory, item.Name);
            if (!File.Exists(downloadedPath))
            {
                downloadedPath = Directory.EnumerateFiles(directory).FirstOrDefault()
                    ?? throw new LocalizedIOException(
                        $"The CLI reported success but downloaded nothing for '{item.Name}'.",
                        LocalizedText.Of(StringKeys.Error.CliNothingDownloaded, item.Name));
            }

            return Read(downloadedPath, item.Path, item.Name);
        }
        finally
        {
            // Best effort: a temp folder we couldn't delete must not turn a working preview into an
            // error. The OS reclaims it, and the next preview uses a fresh GUID either way.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Reads at most <see cref="TextPreviewPolicy.MaxPreviewBytes"/> from the downloaded file and
    /// decodes it. Internal so the reading half can be tested without a download.
    /// </summary>
    internal static TextFilePreview Read(string localPath, string remotePath, string name)
    {
        // One byte past the limit, purely to learn whether there was more.
        var buffer = new byte[TextPreviewPolicy.MaxPreviewBytes + 1];
        int read;
        using (var stream = File.OpenRead(localPath))
        {
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        var truncatedByBytes = read > TextPreviewPolicy.MaxPreviewBytes;
        var length = truncatedByBytes ? (int)TextPreviewPolicy.MaxPreviewBytes : read;
        var bytes = buffer.AsSpan(0, length);

        // A NUL byte anywhere in what we read means this isn't text, whatever the extension said.
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return new TextFilePreview(remotePath, name, string.Empty, 0, read, truncatedByBytes, IsBinary: true, "binary");
        }

        if (truncatedByBytes)
        {
            // Cutting mid-character would make strict UTF-8 decoding fail for the whole file and
            // send it down the Latin-1 path, mojibake and all. Drop the partial sequence instead.
            bytes = bytes[..TrimPartialUtf8Sequence(bytes)];
        }

        var (text, encodingName) = Decode(bytes);
        var (shown, truncatedByLines) = LimitLines(text);
        return new TextFilePreview(
            remotePath,
            name,
            shown,
            CountLines(shown),
            read,
            truncatedByBytes || truncatedByLines,
            IsBinary: false,
            encodingName);
    }

    /// <summary>
    /// UTF-8 first and strictly: a file that decodes as UTF-8 is UTF-8. Only when it doesn't does
    /// Latin-1 take over, which never fails and so must never go first — it would happily turn a
    /// valid UTF-8 "ñ" into two characters.
    /// </summary>
    private static (string Text, string EncodingName) Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "Latin-1");
        }
    }

    /// <summary>
    /// The length to keep so the span doesn't end inside a multi-byte UTF-8 character. Walks back
    /// over trailing continuation bytes (10xxxxxx) to find the lead byte that started that run,
    /// then checks whether the lead byte's own length declaration is satisfied by the continuation
    /// bytes actually present — a completed character (the cut landed exactly on a boundary) is
    /// left alone; an interrupted one is dropped starting at its lead byte.
    /// </summary>
    private static int TrimPartialUtf8Sequence(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        if (length == 0)
        {
            return 0;
        }

        // Walk back from the end over continuation bytes (10xxxxxx) to the byte that started this
        // last character — at most three of them, since no UTF-8 character has more.
        var leadIndex = length - 1;
        var continuationBytes = 0;
        while (leadIndex >= 0 && continuationBytes < 3 && (bytes[leadIndex] & 0xC0) == 0x80)
        {
            leadIndex--;
            continuationBytes++;
        }

        if (leadIndex < 0)
        {
            // Nothing but continuation bytes in the whole span: no lead byte in view at all.
            return 0;
        }

        var leadByte = bytes[leadIndex];
        var expectedLength =
            (leadByte & 0x80) == 0x00 ? 1 :       // 0xxxxxxx
            (leadByte & 0xE0) == 0xC0 ? 2 :       // 110xxxxx
            (leadByte & 0xF0) == 0xE0 ? 3 :       // 1110xxxx
            (leadByte & 0xF8) == 0xF0 ? 4 :       // 11110xxx
            -1;                                    // not a valid lead byte at all

        // Bytes from the lead byte through the end of the span, i.e. how many of the character's
        // declared length are actually present.
        var actualLength = length - leadIndex;

        // A complete character reaches exactly to the end of the span: keep the whole thing.
        // Anything else — an invalid lead byte, or one whose declared length isn't fully backed by
        // continuation bytes — is cut short, so drop it starting at the lead byte.
        return expectedLength == actualLength ? length : leadIndex;
    }

    private static (string Text, bool Truncated) LimitLines(string text)
    {
        var index = 0;
        for (var line = 0; line < TextPreviewPolicy.MaxPreviewLines; line++)
        {
            var next = text.IndexOf('\n', index);
            if (next < 0)
            {
                return (text, false);
            }

            index = next + 1;
        }

        // `index` sits just past the newline that ended the last line we keep; drop that newline so
        // the viewer's own "truncated" note doesn't follow a blank line.
        return (text[..(index - 1)], true);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        // A trailing newline ends the last line rather than starting an empty one: "a\nb\n" is two
        // lines, not three.
        return text[^1] == '\n' ? lines : lines + 1;
    }
}
