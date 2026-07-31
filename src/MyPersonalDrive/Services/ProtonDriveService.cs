using System.Text.Json;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

public sealed class ProtonDriveService
{
    private readonly IProtonDriveCliExecutor _executor;
    private readonly bool _strictListingParsing;

    public ProtonDriveService(IProtonDriveCliExecutor executor, bool strictListingParsing = false)
    {
        _executor = executor;
        _strictListingParsing = strictListingParsing;
        _executor.CommandStarted += (_, args) => CommandStarted?.Invoke(this, args);
        _executor.CommandOutput += (_, args) => CommandOutput?.Invoke(this, args);
        _executor.CommandFinished += (_, args) => CommandFinished?.Invoke(this, args);
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <summary>
    /// Raised when the JSON listing parser fell back to the best-effort text parser, or when
    /// an unrecognized-but-valid JSON shape was encountered. Surfaced to the UI so silent
    /// mis-parses are visible instead of just producing a wrong-looking listing.
    /// </summary>
    public event EventHandler<string>? ListingParseWarning;

    public async Task<IReadOnlyList<DriveItem>> LoadFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var output = await _executor.ExecuteAsync(["filesystem", "list", "--json", path], cancellationToken);
        return ParseListing(output, path);
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["auth", "login"], cancellationToken, timeout: Timeout.InfiniteTimeSpan);

    public Task LogoutAsync(CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["auth", "logout"], cancellationToken);

    public Task<IReadOnlyList<DriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken = default)
        => LoadFolderAsync(path, cancellationToken);

    public Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["filesystem", "download", path, localFolder], cancellationToken);

    public Task TrashItemAsync(string path, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["filesystem", "trash", path], cancellationToken);

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["filesystem", "rename", path, newName], cancellationToken);

    public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
        => _executor.ExecuteAsync(["filesystem", "create-folder", parentPath, name], cancellationToken);

    public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "filesystem", "copy" };
        if (!string.IsNullOrEmpty(newName))
        {
            arguments.Add("-n");
            arguments.Add(newName);
        }

        arguments.Add(sourcePath);
        arguments.Add(targetParentPath);
        return _executor.ExecuteAsync(arguments, cancellationToken);
    }

    public Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "filesystem", "upload" };
        var strategyFlag = strategy switch
        {
            UploadConflictStrategy.KeepBoth => "keep-both",
            UploadConflictStrategy.Replace => "replace",
            UploadConflictStrategy.Skip => "skip",
            _ => null
        };

        if (strategyFlag is not null)
        {
            arguments.Add("-c");
            arguments.Add(strategyFlag);
        }

        arguments.AddRange(localPaths);
        arguments.Add(parentPath);
        return _executor.ExecuteAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// A listing response can fail to parse in two structurally different ways, and conflating
    /// them is what used to make an empty (but validly parsed) folder fall through to the
    /// text-heuristic parser and come out looking non-empty. See docs/PLAN-TECH-DEBT.md B2.1.
    /// </summary>
    private enum ListingParseOutcome
    {
        /// <summary>Valid JSON in a recognized shape. <c>items</c> is authoritative, even when empty.</summary>
        Parsed,

        /// <summary>The output isn't JSON at all (e.g. the CLI ignored --json). Fall back to the text parser.</summary>
        NotJson,

        /// <summary>Valid JSON, but not a shape we know how to read. Never guess: treat as an error.</summary>
        Malformed
    }

    private IReadOnlyList<DriveItem> ParseListing(string output, string parentPath)
    {
        var outcome = TryParseJsonListing(output, parentPath, out var items);
        switch (outcome)
        {
            case ListingParseOutcome.Parsed:
                return items;

            case ListingParseOutcome.NotJson when !_strictListingParsing:
                ListingParseWarning?.Invoke(this, $"CLI output for '{parentPath}' was not JSON; using best-effort text parsing. First line: {FirstLine(output)}");
                return ParseTextListing(output, parentPath);

            case ListingParseOutcome.NotJson:
                throw new InvalidOperationException($"Expected JSON output for '{parentPath}' but got non-JSON text. First line: {FirstLine(output)}");

            default:
                throw new InvalidOperationException($"Could not interpret the Proton Drive CLI listing for '{parentPath}': unrecognized JSON shape.");
        }
    }

    private static ListingParseOutcome TryParseJsonListing(string output, string parentPath, out IReadOnlyList<DriveItem> items)
    {
        items = Array.Empty<DriveItem>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return ListingParseOutcome.NotJson;
        }

        using (document)
        {
            var root = document.RootElement;

            JsonElement? entriesArray = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when TryGetArray(root, "items", out var array) => array,
                JsonValueKind.Object when TryGetArray(root, "entries", out var array) => array,
                JsonValueKind.Object when TryGetArray(root, "children", out var array) => array,
                _ => null
            };

            if (entriesArray is null)
            {
                return ListingParseOutcome.Malformed;
            }

            var parsed = new List<DriveItem>();
            foreach (var entry in entriesArray.Value.EnumerateArray())
            {
                if (TryParseJsonEntry(entry, parentPath, out var item))
                {
                    parsed.Add(item);
                }
            }

            items = parsed;
            return ListingParseOutcome.Parsed;
        }
    }

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOfAny(['\r', '\n']);
        var firstLine = newlineIndex < 0 ? text : text[..newlineIndex];
        return firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine;
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

    public static string CombinePath(string parentPath, string name)
    {
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
        {
            return "/" + name;
        }

        return parentPath.TrimEnd('/') + "/" + name;
    }
}
