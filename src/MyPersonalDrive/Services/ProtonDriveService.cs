using System.Text.Json;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

public sealed class ProtonDriveService
{
    private readonly IProtonDriveCliExecutor _executor;

    public ProtonDriveService(IProtonDriveCliExecutor executor)
    {
        _executor = executor;
        _executor.CommandStarted += (_, args) => CommandStarted?.Invoke(this, args);
        _executor.CommandOutput += (_, args) => CommandOutput?.Invoke(this, args);
        _executor.CommandFinished += (_, args) => CommandFinished?.Invoke(this, args);
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    public async Task<IReadOnlyList<DriveItem>> LoadFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var output = await _executor.ExecuteAsync($"filesystem list --json \"{path}\"", cancellationToken);
        return ParseListing(output, path);
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync("auth login", cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync("auth logout", cancellationToken);

    public Task<IReadOnlyList<DriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken = default)
        => LoadFolderAsync(path, cancellationToken);

    public Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync($"filesystem download {Quote(path)} {Quote(localFolder)}", cancellationToken);

    public Task TrashItemAsync(string path, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync($"filesystem trash {Quote(path)}", cancellationToken);

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync($"filesystem rename {Quote(path)} {Quote(newName)}", cancellationToken);

    public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync($"filesystem create-folder {Quote(parentPath)} {Quote(name)}", cancellationToken);

    public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        var nameArg = string.IsNullOrEmpty(newName) ? "" : $" -n {Quote(newName)}";
        return _executor.ExecuteAsync($"filesystem copy{nameArg} {Quote(sourcePath)} {Quote(targetParentPath)}", cancellationToken);
    }

    public Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default)
    {
        var fileArguments = string.Join(" ", localPaths.Select(Quote));
        var strategyFlag = strategy switch
        {
            UploadConflictStrategy.KeepBoth => " -c keep-both",
            UploadConflictStrategy.Replace => " -c replace",
            UploadConflictStrategy.Skip => " -c skip",
            _ => string.Empty
        };
        return _executor.ExecuteAsync($"filesystem upload{strategyFlag} {fileArguments} {Quote(parentPath)}", cancellationToken);
    }

    private static IReadOnlyList<DriveItem> ParseListing(string output, string parentPath)
    {
        if (TryParseJsonListing(output, parentPath, out var items))
        {
            return items;
        }

        return ParseTextListing(output, parentPath);
    }

    private static bool TryParseJsonListing(string output, string parentPath, out IReadOnlyList<DriveItem> items)
    {
        items = Array.Empty<DriveItem>();

        try
        {
            using var document = JsonDocument.Parse(output);
            var parsed = new List<DriveItem>();
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    if (TryParseJsonEntry(entry, parentPath, out var item))
                    {
                        parsed.Add(item);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, "items", out var itemsArray))
            {
                foreach (var entry in itemsArray.EnumerateArray())
                {
                    if (TryParseJsonEntry(entry, parentPath, out var item))
                    {
                        parsed.Add(item);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, "entries", out var entriesArray))
            {
                foreach (var entry in entriesArray.EnumerateArray())
                {
                    if (TryParseJsonEntry(entry, parentPath, out var item))
                    {
                        parsed.Add(item);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, "children", out var childrenArray))
            {
                foreach (var entry in childrenArray.EnumerateArray())
                {
                    if (TryParseJsonEntry(entry, parentPath, out var item))
                    {
                        parsed.Add(item);
                    }
                }
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement array)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
        {
            array = property;
            return true;
        }

        array = default;
        return false;
    }

    private static bool TryParseJsonEntry(JsonElement entry, string parentPath, out DriveItem item)
    {
        var name = ReadString(entry, "name", "title", "label") ?? string.Empty;
        var path = CombinePath(parentPath, name);
        var type = ReadString(entry, "type", "kind", "entryType") ?? string.Empty;
        var isFolder = type.Contains("folder", StringComparison.OrdinalIgnoreCase)
            || type.Contains("directory", StringComparison.OrdinalIgnoreCase)
            || type.Contains("dir", StringComparison.OrdinalIgnoreCase);
        var size = ReadLong(entry, "size", "bytes");

        item = new DriveItem(
            Path: path,
            Name: name,
            IsFolder: isFolder,
            Size: size,
            ModifiedAt: ReadString(entry, "modifiedAt", "updatedAt", "lastModified"),
            Owner: ReadString(entry, "owner", "user", "createdBy"),
            IsShared: ReadBool(entry, "isShared", "shared", "linkShared"));

        return !string.IsNullOrWhiteSpace(item.Name);
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.TryGetInt64(out var value))
            {
                return value;
            }

            if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var nestedValue) && nestedValue.TryGetInt64(out value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var nestedValue) && nestedValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return nestedValue.GetBoolean();
            }
        }

        return false;
    }

    private static IReadOnlyList<DriveItem> ParseTextListing(string output, string parentPath)
    {
        var items = new List<DriveItem>();
        using var reader = new StringReader(output);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            var isFolder = trimmed.Contains("🗂", StringComparison.Ordinal);
            var name = trimmed[(trimmed.LastIndexOf(' ') + 1)..];
            var path = CombinePath(parentPath, name);

            items.Add(new DriveItem(path, name, isFolder));
        }

        return items;
    }

    private static string CombinePath(string parentPath, string name)
    {
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
        {
            return "/" + name;
        }

        return parentPath.TrimEnd('/') + "/" + name;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
