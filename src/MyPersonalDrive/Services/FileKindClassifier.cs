using System.Collections.Frozen;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Maps a node name to a <see cref="FileKind"/>. Pure and static: no I/O, no CLI, no Avalonia — the
/// listing and the metrics calculator both go through here so a `.webm` is a video in exactly one
/// place. See docs/PLAN-BROWSER-VIEWS.md V3/M1.
/// </summary>
public static class FileKindClassifier
{
    /// <summary>
    /// Two-segment extensions, checked before the single-segment table so `.tar.gz` is an archive
    /// rather than whatever `.gz` alone would say.
    /// </summary>
    private static readonly FrozenSet<string> CompoundArchiveExtensions = new[]
    {
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz4",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FileKind> ByExtension = new Dictionary<string, FileKind>(StringComparer.Ordinal)
    {
        [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".png"] = FileKind.Image,
        [".gif"] = FileKind.Image, [".bmp"] = FileKind.Image, [".webp"] = FileKind.Image,
        [".svg"] = FileKind.Image, [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image,
        [".heic"] = FileKind.Image, [".heif"] = FileKind.Image, [".avif"] = FileKind.Image,
        [".ico"] = FileKind.Image, [".raw"] = FileKind.Image, [".cr2"] = FileKind.Image,
        [".nef"] = FileKind.Image, [".dng"] = FileKind.Image, [".psd"] = FileKind.Image,

        [".mp4"] = FileKind.Video, [".mkv"] = FileKind.Video, [".webm"] = FileKind.Video,
        [".avi"] = FileKind.Video, [".mov"] = FileKind.Video, [".wmv"] = FileKind.Video,
        [".flv"] = FileKind.Video, [".mpg"] = FileKind.Video, [".mpeg"] = FileKind.Video,
        [".m4v"] = FileKind.Video, [".3gp"] = FileKind.Video, [".ts"] = FileKind.Video,

        [".mp3"] = FileKind.Audio, [".flac"] = FileKind.Audio, [".wav"] = FileKind.Audio,
        [".aac"] = FileKind.Audio, [".ogg"] = FileKind.Audio, [".oga"] = FileKind.Audio,
        [".opus"] = FileKind.Audio, [".m4a"] = FileKind.Audio, [".wma"] = FileKind.Audio,
        [".aiff"] = FileKind.Audio, [".mid"] = FileKind.Audio, [".midi"] = FileKind.Audio,

        [".pdf"] = FileKind.Pdf,

        [".doc"] = FileKind.Document, [".docx"] = FileKind.Document, [".odt"] = FileKind.Document,
        [".rtf"] = FileKind.Document, [".pages"] = FileKind.Document, [".epub"] = FileKind.Document,
        [".mobi"] = FileKind.Document, [".azw3"] = FileKind.Document, [".djvu"] = FileKind.Document,

        [".xls"] = FileKind.Spreadsheet, [".xlsx"] = FileKind.Spreadsheet, [".xlsm"] = FileKind.Spreadsheet,
        [".ods"] = FileKind.Spreadsheet, [".csv"] = FileKind.Spreadsheet, [".tsv"] = FileKind.Spreadsheet,
        [".numbers"] = FileKind.Spreadsheet,

        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation,
        [".odp"] = FileKind.Presentation, [".key"] = FileKind.Presentation,

        [".zip"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".7z"] = FileKind.Archive,
        [".gz"] = FileKind.Archive, [".bz2"] = FileKind.Archive, [".xz"] = FileKind.Archive,
        [".zst"] = FileKind.Archive, [".tar"] = FileKind.Archive, [".iso"] = FileKind.Archive,
        [".deb"] = FileKind.Archive, [".rpm"] = FileKind.Archive, [".dmg"] = FileKind.Archive,
        [".apk"] = FileKind.Archive, [".jar"] = FileKind.Archive, [".appimage"] = FileKind.Archive,

        [".c"] = FileKind.Code, [".h"] = FileKind.Code, [".cpp"] = FileKind.Code,
        [".hpp"] = FileKind.Code, [".cs"] = FileKind.Code, [".java"] = FileKind.Code,
        [".kt"] = FileKind.Code, [".py"] = FileKind.Code, [".rb"] = FileKind.Code,
        [".go"] = FileKind.Code, [".rs"] = FileKind.Code, [".php"] = FileKind.Code,
        [".js"] = FileKind.Code, [".mjs"] = FileKind.Code, [".jsx"] = FileKind.Code,
        [".tsx"] = FileKind.Code, [".swift"] = FileKind.Code, [".sh"] = FileKind.Code,
        [".bash"] = FileKind.Code, [".zsh"] = FileKind.Code, [".ps1"] = FileKind.Code,
        [".sql"] = FileKind.Code, [".html"] = FileKind.Code, [".htm"] = FileKind.Code,
        [".css"] = FileKind.Code, [".scss"] = FileKind.Code, [".json"] = FileKind.Code,
        [".xml"] = FileKind.Code, [".yaml"] = FileKind.Code, [".yml"] = FileKind.Code,
        [".toml"] = FileKind.Code, [".ini"] = FileKind.Code, [".patch"] = FileKind.Code,
        [".diff"] = FileKind.Code, [".axaml"] = FileKind.Code, [".xaml"] = FileKind.Code,

        [".txt"] = FileKind.Text, [".md"] = FileKind.Text, [".log"] = FileKind.Text,
        [".conf"] = FileKind.Text, [".cfg"] = FileKind.Text, [".nfo"] = FileKind.Text,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static FileKind Classify(string name, bool isFolder)
    {
        if (isFolder)
        {
            return FileKind.Folder;
        }

        var extension = ExtensionOf(name);
        if (extension.Length == 0)
        {
            return FileKind.Other;
        }

        if (CompoundArchiveExtensions.Contains(extension))
        {
            return FileKind.Archive;
        }

        // For a two-segment extension we don't know as a whole, the last segment decides
        // (".tar.gz" is already handled above, so this is ".something.unknown"). If that segment
        // means nothing either, fall back to the first one: ".tar.wat" is still a tarball.
        var lastDot = extension.LastIndexOf('.');
        if (lastDot > 0)
        {
            if (ByExtension.TryGetValue(extension[lastDot..], out var tailKind))
            {
                return tailKind;
            }

            return ByExtension.TryGetValue(extension[..lastDot], out var headKind) ? headKind : FileKind.Other;
        }

        return ByExtension.TryGetValue(extension, out var kind) ? kind : FileKind.Other;
    }

    /// <summary>
    /// The extension, lowercased with <see cref="string.ToLowerInvariant"/> — never the
    /// culture-sensitive overload, which in a Turkish locale would turn ".JPG" into ".jpg" with a
    /// dotless i and silently misclassify it. Returns up to two segments (".tar.gz"), and empty
    /// for a name with no extension or for a dotfile like ".bashrc", whose leading dot names the
    /// file rather than typing it.
    /// </summary>
    private static string ExtensionOf(string name)
    {
        var trimmed = name.AsSpan().TrimEnd();
        if (trimmed.IsEmpty)
        {
            return string.Empty;
        }

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == trimmed.Length - 1)
        {
            // No dot, a leading dot (dotfile), or a trailing dot: no extension either way.
            return string.Empty;
        }

        var stem = trimmed[..lastDot];
        var secondDot = stem.LastIndexOf('.');
        if (secondDot > 0 && lastDot - secondDot <= 5)
        {
            // Only take the second segment if it's short enough to plausibly be an extension:
            // "backup.2026-01-02.tar" must not become ".2026-01-02.tar".
            return trimmed[secondDot..].ToString().ToLowerInvariant();
        }

        return trimmed[lastDot..].ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The label shown in the metrics histogram. Spanish, matching the settings view's copy.
    /// </summary>
    public static string DisplayName(FileKind kind) => kind switch
    {
        FileKind.Folder => "Carpetas",
        FileKind.Image => "Imágenes",
        FileKind.Video => "Vídeos",
        FileKind.Audio => "Audio",
        FileKind.Document => "Documentos",
        FileKind.Spreadsheet => "Hojas de cálculo",
        FileKind.Presentation => "Presentaciones",
        FileKind.Pdf => "PDF",
        FileKind.Archive => "Archivos comprimidos",
        FileKind.Code => "Código",
        FileKind.Text => "Texto",
        _ => "Otros",
    };
}
