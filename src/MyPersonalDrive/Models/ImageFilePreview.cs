namespace MyPersonalDrive.Models;

/// <summary>
/// The decoded bytes of a remote image, as shown by the in-app viewer. Produced by
/// <c>Services.ImageFilePreviewService</c>, which downloads the file to a temp folder, reads it and
/// deletes it again — the preview never keeps a copy on disk. Kept as raw bytes rather than a
/// decoded bitmap: view models never touch Avalonia types (AGENTS.md), so turning the bytes into a
/// <c>Bitmap</c> is the View's job (see <c>Views.Converters.BytesToBitmapConverter</c>).
/// </summary>
/// <param name="Path">The remote path the preview came from.</param>
/// <param name="Name">The file's name, for the viewer's header.</param>
/// <param name="Bytes">The image file's raw bytes, undecoded.</param>
/// <param name="ByteCount">Bytes read from the downloaded file.</param>
public sealed record ImageFilePreview(
    string Path,
    string Name,
    byte[] Bytes,
    long ByteCount);
