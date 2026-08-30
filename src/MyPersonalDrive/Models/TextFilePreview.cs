namespace MyPersonalDrive.Models;

/// <summary>
/// The decoded contents of a remote plain-text file, as shown by the in-app viewer. Produced by
/// <c>Services.TextFilePreviewService</c>, which downloads the file to a temp folder, reads it and
/// deletes it again — the preview never keeps a copy on disk.
/// </summary>
/// <param name="Path">The remote path the preview came from.</param>
/// <param name="Name">The file's name, for the viewer's header.</param>
/// <param name="Text">The decoded text, already truncated to the policy's limits.</param>
/// <param name="LineCount">Lines in <paramref name="Text"/> (not in the whole file, when truncated).</param>
/// <param name="ByteCount">
/// Bytes read from the downloaded file, before decoding — capped at one past the policy's byte
/// limit, since that's where the reader stops. Not the file's size when it was truncated.
/// </param>
/// <param name="IsTruncated">True when the file was longer than the byte or line limit.</param>
/// <param name="IsBinary">
/// True when the bytes don't look like text at all, in which case <paramref name="Text"/> is empty
/// and the viewer says so instead of rendering control characters.
/// </param>
/// <param name="EncodingName">How the bytes were decoded ("UTF-8" or the fallback).</param>
public sealed record TextFilePreview(
    string Path,
    string Name,
    string Text,
    int LineCount,
    long ByteCount,
    bool IsTruncated,
    bool IsBinary,
    string EncodingName);
