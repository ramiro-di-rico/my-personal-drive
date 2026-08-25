using System.Text.Json;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.Proton;

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
    /// See <see cref="IProtonDriveCliExecutor.ResetRemoteCacheAsync"/>. Exposed here because the
    /// scanner holds a service, not an executor, and it is the scanner that knows when a fresh view
    /// of the remote tree is required.
    /// </summary>
    public Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default)
        => _executor.ResetRemoteCacheAsync(cancellationToken);

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

    /// <summary>
    /// The version string the CLI reports for itself. Captured from a real binary:
    ///
    /// <code>
    /// $ proton-drive --version
    /// Proton Drive CLI cli-drive@0.6.0+f8e16aac
    /// Proton Drive SDK js@0.19.2+f8e16aac
    /// </code>
    ///
    /// Only the first non-empty line is returned — the second is the bundled SDK's version, which
    /// is not what "which CLI am I running" means. The line is left unparsed on purpose: the app
    /// only displays it, and splitting `cli-drive@0.6.0+f8e16aac` into fields would invent a
    /// format contract the CLI has not promised. Returns null when the CLI printed nothing.
    /// </summary>
    public async Task<string?> GetCliVersionAsync(CancellationToken cancellationToken = default)
    {
        var output = await _executor.ExecuteAsync(["--version"], cancellationToken);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
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

    /// <summary>
    /// Moves nodes into another folder, keeping their names. Per docs/PLAN-LOCAL-SYNC.md
    /// Appendix A #3/#8 this preserves each node's <c>uid</c> (only <c>parentUid</c> changes),
    /// which is what makes a remote move a single cheap call instead of copy+trash. The CLI
    /// takes the target parent folder last, after one or more source paths.
    /// </summary>
    public Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one source path is required.", nameof(paths));
        }

        var arguments = new List<string> { "filesystem", "move" };
        arguments.AddRange(paths);
        arguments.Add(targetParentPath);
        return _executor.ExecuteAsync(arguments, cancellationToken);
    }

    public Task MoveItemAsync(string path, string targetParentPath, CancellationToken cancellationToken = default)
        => MoveItemsAsync([path], targetParentPath, cancellationToken);

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

    /// <summary>
    /// Reads one entry using the real shape verified against cli-drive@0.4.2 in
    /// docs/PLAN-LOCAL-SYNC.md Appendix A. Fields are read by their confirmed real names, not
    /// guessed aliases (see B2.4 in docs/PLAN-TECH-DEBT.md) — an entry whose shape doesn't
    /// match is skipped rather than silently fabricated, since a wrong guess here is exactly
    /// the class of bug that made an empty folder look non-empty before B2.1.
    /// </summary>
    private static bool TryParseJsonEntry(JsonElement entry, string parentPath, out DriveItem item)
    {
        var nodeId = ReadPlainString(entry, "uid");
        // The CLI's own docs say a name can fail to decrypt or collide; it falls back to the
        // node UID as the addressable name in that case (`--help`: "node UIDs can be used
        // instead"). Silently dropping such an entry would make it invisible to sync, which is
        // the same "phantom delete" failure mode B2.1 fixed for the whole-listing case.
        var name = ReadOkString(entry, "name") ?? nodeId ?? string.Empty;
        var path = CombinePath(parentPath, name);
        var type = ReadPlainString(entry, "type") ?? string.Empty;
        var isFolder = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase);
        var owner = ReadNestedPlainString(entry, "ownedBy", "email");
        var isShared = ReadPlainBool(entry, "isShared");

        long? size = null;
        DateTimeOffset? modifiedAt = null;
        string? contentHash = null;

        if (TryGetOkObject(entry, "activeRevision", out var revision))
        {
            size = ReadPlainLong(revision, "claimedSize");
            modifiedAt = ReadPlainDateTimeOffset(revision, "claimedModificationTime");
            contentHash = ReadNestedPlainString(revision, "claimedDigests", "sha1");
        }

        // Folders have no activeRevision; their claimed mtime (when present at all) lives at
        // `folder.claimedModificationTime`. Fall back further to the server-side
        // `modificationTime`, which is good enough for folders since they aren't diffed by
        // content the way files are.
        modifiedAt ??= ReadPlainDateTimeOffset(entry, "folder", "claimedModificationTime")
            ?? ReadPlainDateTimeOffset(entry, "modificationTime");

        item = new DriveItem(
            Path: path,
            Name: name,
            IsFolder: isFolder,
            Size: size,
            ModifiedAt: modifiedAt,
            Owner: owner,
            IsShared: isShared,
            NodeId: nodeId,
            ContentHash: contentHash);

        return !string.IsNullOrWhiteSpace(item.Name);
    }

    /// <summary>Unwraps the CLI's `{ ok, value }` unwrap pattern used for decryptable string fields.</summary>
    private static string? ReadOkString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            && property.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>Unwraps the CLI's `{ ok, value }` pattern where `value` is itself an object (e.g. `activeRevision`).</summary>
    private static bool TryGetOkObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            && property.TryGetProperty("value", out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadPlainString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>Reads a plain (non-`{ok,value}`-wrapped) nested string, e.g. `ownedBy.email`.</summary>
    private static string? ReadNestedPlainString(JsonElement element, string propertyName, string nestedPropertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? ReadPlainString(property, nestedPropertyName)
            : null;

    private static long? ReadPlainLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static bool ReadPlainBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();

    private static DateTimeOffset? ReadPlainDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ReadPlainDateTimeOffset(JsonElement element, string propertyName, string nestedPropertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? ReadPlainDateTimeOffset(property, nestedPropertyName)
            : null;

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

    /// <summary>
    /// Builds a remote path, escaping any <c>/</c> inside the node's own name as <c>\/</c> — the
    /// convention `filesystem list --help` documents ("Escape / in node names with a backslash",
    /// example `/my-files/folder/foo\/bar`).
    ///
    /// Without this, a node genuinely named <c>foo/bar</c> produced a path indistinguishable from a
    /// folder <c>foo</c> containing <c>bar</c>, so every command aimed at it — download, rename,
    /// trash — failed with "Node not found". Not hypothetical, just unobserved: Proton allows the
    /// character, only the path syntax needs the escape.
    /// </summary>
    public static string CombinePath(string parentPath, string name)
    {
        var escapedName = EscapeNodeName(name);
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
        {
            return "/" + escapedName;
        }

        return parentPath.TrimEnd('/') + "/" + escapedName;
    }

    /// <summary>
    /// True when a node's name contains a character that makes it unrepresentable as a local file.
    /// On Linux <c>/</c> is the one byte a filename may not contain, so such a node cannot be
    /// mirrored under its real name at all — which is why the sync engine skips them rather than
    /// inventing a substitute (see <see cref="Sync.RemoteScanner"/>).
    /// </summary>
    public static bool HasUnmappableName(string name) => name.Contains('/', StringComparison.Ordinal);

    /// <summary>
    /// Escapes the path-separator meaning out of a node's own name.
    /// </summary>
    /// <remarks>
    /// Only <c>/</c> is escaped, because that is the only escape the CLI documents. A name containing
    /// the literal two characters <c>\/</c> would therefore round-trip ambiguously — unresolvable
    /// without knowing the CLI's exact unescaping, and not worth guessing at for a case Proton's own
    /// clients would struggle to create.
    /// </remarks>
    private static string EscapeNodeName(string name)
        => name.Contains('/', StringComparison.Ordinal) ? name.Replace("/", "\\/", StringComparison.Ordinal) : name;
}
