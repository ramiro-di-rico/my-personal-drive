using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MyPersonalDrive.Models;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// <see cref="IDriveOperations"/> over Microsoft Graph. One method per operation, same shape as
/// <c>Providers.Proton.ProtonDriveService</c> but a Graph request instead of a CLI process — see
/// docs/PLAN-CLOUD-PROVIDERS.md §4.3 for the request-by-request mapping this follows.
/// </summary>
public sealed class OneDriveOperations : IDriveOperations, IDeltaSource
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0/me/drive";

    /// <summary>Graph's own single-request upload ceiling; larger files go through a chunked upload session.</summary>
    private const long SmallUploadCeilingBytes = 4L * 1024 * 1024;

    /// <summary>Must be a multiple of 320 KiB per Graph's chunked-upload contract.</summary>
    private const int UploadChunkSizeBytes = 10 * 320 * 1024; // 3,200,000 bytes ≈ 3.05 MiB

    private readonly GraphHttpClient _http;
    private readonly OneDrivePathSyntax _paths = new();

    /// <summary>One GET per distinct target parent, cached per <see cref="OneDriveOperations"/> instance — <c>Capabilities.SupportsBatchMove = false</c>, so the caller already expects one request per item, not per target.</summary>
    private readonly Dictionary<string, string> _targetIdCache = new(StringComparer.Ordinal);

    public OneDriveOperations(GraphHttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var items = new List<DriveItem>();
        var url = $"{ItemSegment(path)}/children?$select=id,name,size,file,folder,fileSystemInfo,parentReference,shared,createdBy&$top=200";

        // Must be followed to exhaustion: a partial listing reads as a remote deletion to the sync
        // reconciler, the same failure mode PLAN-LOCAL-SYNC.md Appendix A #16 already warns about
        // for Proton's own stale-cache behavior.
        while (url is not null)
        {
            using var response = await _http.SendAsync($"GET {DescribePath(path)}/children", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var page = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphItemsPage, cancellationToken)
                ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, "OneDrive returned an empty listing page.", DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpEmptyListingPage, "OneDrive") };

            foreach (var item in page.Value)
            {
                items.Add(ToDriveItem(item, path));
            }

            url = page.NextLink;
        }

        return items;
    }

    public async Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
    {
        var name = PathName(path);
        var url = $"{ItemSegment(path)}/content";
        using var response = await _http.SendAsync($"GET {DescribePath(path)}/content", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        // A successful GET here is either the file directly, or (per Graph's documented behavior)
        // GraphHttpClient's HttpClient already followed the 302 to the pre-authenticated download
        // URL automatically — HttpClient follows redirects by default and, critically, drops the
        // Authorization header when the redirect target's host differs, which is exactly the
        // "without the auth header" requirement in docs/PLAN-CLOUD-PROVIDERS.md §4.3.
        Directory.CreateDirectory(localFolder);
        var localPath = Path.Combine(localFolder, name);
        await using var target = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await response.Content.CopyToAsync(target, cancellationToken);
    }

    public async Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default)
    {
        foreach (var localPath in localPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadOneAsync(localPath, parentPath, strategy, cancellationToken);
        }
    }

    private async Task UploadOneAsync(string localPath, string parentPath, UploadConflictStrategy strategy, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(localPath);
        var conflictBehavior = ConflictBehaviorFor(strategy);
        var fileInfo = new FileInfo(localPath);

        try
        {
            if (fileInfo.Length <= SmallUploadCeilingBytes)
            {
                await UploadSmallAsync(localPath, parentPath, name, conflictBehavior, cancellationToken);
            }
            else
            {
                await UploadChunkedAsync(localPath, parentPath, name, conflictBehavior, cancellationToken);
            }
        }
        catch (DriveException ex) when (ex.Kind == DriveErrorKind.AlreadyExists && strategy == UploadConflictStrategy.Skip)
        {
            // Proton's own "skip" conflict behavior silently succeeds; Graph's "fail" conflict
            // behavior instead returns 409, which GraphErrorClassifier maps to AlreadyExists.
            // Translating that back into success here keeps the two providers' Skip behavior
            // identical from the caller's point of view (docs/PLAN-CLOUD-PROVIDERS.md §4.3's
            // documented asymmetry).
        }
    }

    private async Task UploadSmallAsync(string localPath, string parentPath, string name, string conflictBehavior, CancellationToken cancellationToken)
    {
        var url = $"{ItemSegment(_paths.Combine(parentPath, name))}/content?@microsoft.graph.conflictBehavior={conflictBehavior}";
        using var response = await _http.SendAsync(
            $"PUT {DescribePath(parentPath)}/{name}:/content",
            () => new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(File.OpenRead(localPath)) },
            cancellationToken);
        response.Content.Dispose();
    }

    private async Task UploadChunkedAsync(string localPath, string parentPath, string name, string conflictBehavior, CancellationToken cancellationToken)
    {
        var createSessionUrl = $"{ItemSegment(_paths.Combine(parentPath, name))}/createUploadSession";
        using var sessionResponse = await _http.SendAsync(
            $"POST {DescribePath(parentPath)}/{name}:/createUploadSession",
            () => new HttpRequestMessage(HttpMethod.Post, createSessionUrl)
            {
                Content = JsonContent.Create(
                    new GraphCreateUploadSessionRequest { Item = new GraphUploadSessionItem { Name = name, ConflictBehavior = conflictBehavior } },
                    AppJsonContext.Default.GraphCreateUploadSessionRequest),
            },
            cancellationToken);
        var session = await sessionResponse.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphUploadSession, cancellationToken)
            ?? throw new DriveException(createSessionUrl, (int)sessionResponse.StatusCode, string.Empty, string.Empty, "OneDrive did not return an upload session.", DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpNoUploadSession, "OneDrive", string.Empty) };

        await using var stream = File.OpenRead(localPath);
        var totalLength = stream.Length;
        var buffer = new byte[UploadChunkSizeBytes];
        long offset = 0;

        while (offset < totalLength)
        {
            var chunkLength = (int)Math.Min(UploadChunkSizeBytes, totalLength - offset);
            var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, chunkLength), chunkLength, throwOnEndOfStream: false, cancellationToken);
            var rangeEnd = offset + read - 1;

            using var chunkContent = new ByteArrayContent(buffer, 0, read);
            chunkContent.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, rangeEnd, totalLength);
            // Deliberately not routed through GraphHttpClient.SendAsync: the upload session URL is
            // pre-authenticated and must NOT carry the bearer header (docs/PLAN-CLOUD-PROVIDERS.md
            // §4.3), and it needs no 401-refresh handling since Graph itself doesn't own the URL.
            using var chunkResponse = await _http.SendUnauthenticatedAsync(
                new HttpRequestMessage(HttpMethod.Put, session.UploadUrl) { Content = chunkContent },
                cancellationToken);

            if (!chunkResponse.IsSuccessStatusCode)
            {
                var body = await chunkResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new DriveException(session.UploadUrl, (int)chunkResponse.StatusCode, string.Empty, body,
                    $"The upload of {name} to OneDrive failed at byte {offset}.", GraphErrorClassifier.Classify(chunkResponse.StatusCode, body)) { Detail = LocalizedText.Of(StringKeys.Error.OpUploadFailedAtByte, "OneDrive", name, offset) };
            }

            offset += read;
        }
    }

    public async Task TrashItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var url = ItemSegment(path);
        using var response = await _http.SendAsync($"DELETE {DescribePath(path)}", () => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken);
        response.Content.Dispose();
    }

    public async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var url = ItemSegment(path);
        using var response = await _http.SendAsync(
            $"PATCH {DescribePath(path)}",
            () => new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(new GraphRenameRequest { Name = newName }, AppJsonContext.Default.GraphRenameRequest) },
            cancellationToken);
        response.Content.Dispose();
    }

    public async Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var url = $"{ItemSegment(parentPath)}/children";
        using var response = await _http.SendAsync(
            $"POST {DescribePath(parentPath)}/children",
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new GraphCreateFolderRequest { Name = name }, AppJsonContext.Default.GraphCreateFolderRequest),
            },
            cancellationToken);
        response.Content.Dispose();
    }

    public async Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default)
    {
        var targetId = await GetItemIdAsync(targetParentPath, cancellationToken);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = ItemSegment(path);
            using var response = await _http.SendAsync(
                $"PATCH {DescribePath(path)} (move)",
                () => new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = JsonContent.Create(new GraphMoveRequest { ParentReference = new GraphParentReferenceRequest { Id = targetId } }, AppJsonContext.Default.GraphMoveRequest),
                },
                cancellationToken);
            response.Content.Dispose();
        }
    }

    public async Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        var targetId = await GetItemIdAsync(targetParentPath, cancellationToken);
        var url = $"{ItemSegment(sourcePath)}/copy";

        using var response = await _http.SendAsync(
            $"POST {DescribePath(sourcePath)}/copy",
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new GraphCopyRequest { ParentReference = new GraphParentReferenceRequest { Id = targetId }, Name = newName }, AppJsonContext.Default.GraphCopyRequest),
            },
            cancellationToken);

        // Graph's copy is asynchronous: 202 Accepted + a Location header pointing at a monitor URL,
        // which this polls until completed/failed so the interface's Task stays synchronous from
        // the caller's point of view (Capabilities.CopyIsAsynchronous = true — the caller doesn't
        // need to know).
        var monitorUrl = response.Headers.Location?.ToString();
        response.Content.Dispose();
        if (monitorUrl is null)
        {
            return;
        }

        await PollCopyMonitorAsync(monitorUrl, sourcePath, cancellationToken);
    }

    private async Task PollCopyMonitorAsync(string monitorUrl, string sourcePath, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        const int maxAttempts = 60; // ~a few minutes of backoff-capped polling before giving up

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The monitor URL is itself pre-authenticated per Graph's documented copy-status
            // contract, same as a download redirect or an upload session — no bearer header.
            using var response = await _http.SendUnauthenticatedAsync(new HttpRequestMessage(HttpMethod.Get, monitorUrl), cancellationToken);
            if (response.StatusCode == HttpStatusCode.SeeOther || response.IsSuccessStatusCode && response.Headers.Location is not null)
            {
                return; // redirected to the finished item — done
            }

            var status = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphCopyMonitorStatus, cancellationToken);
            if (status is null)
            {
                return;
            }

            if (string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new DriveException(monitorUrl, 0, string.Empty, string.Empty, $"The copy of {sourcePath} on OneDrive failed.", DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpCopyFailed, "OneDrive", sourcePath) };
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
        }

        throw new DriveException(monitorUrl, 0, string.Empty, string.Empty, $"The copy of {sourcePath} on OneDrive did not finish in time.", DriveErrorKind.Timeout) { Detail = LocalizedText.Of(StringKeys.Error.OpCopyTimeout, "OneDrive", sourcePath) };
    }

    public async Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var url = $"{ItemSegment(path)}/createLink";
        using var response = await _http.SendAsync(
            $"POST {DescribePath(path)}/createLink",
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new GraphSharingLinkRequest(), AppJsonContext.Default.GraphSharingLinkRequest),
            },
            cancellationToken);

        var permission = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphPermission, cancellationToken);
        return permission?.Link?.WebUrl is { Length: > 0 } webUrl
            ? webUrl
            : throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"OneDrive did not return a sharing link for {path}.", DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpNoShareLink, "OneDrive", path) };
    }

    private async Task<string> GetItemIdAsync(string path, CancellationToken cancellationToken)
    {
        if (_targetIdCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var url = $"{ItemSegment(path)}?$select=id";
        using var response = await _http.SendAsync($"GET {DescribePath(path)} (resolve id)", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var item = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphDriveItem, cancellationToken)
            ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"OneDrive did not return an id for {path}.", DriveErrorKind.NotFound) { Detail = LocalizedText.Of(StringKeys.Error.OpNoId, "OneDrive", path) };

        _targetIdCache[path] = item.Id;
        return item.Id;
    }

    /// <summary>Same field list as <see cref="ListFolderAsync"/> plus `deleted`, which only a delta page ever populates.</summary>
    private const string DeltaSelectFields = "id,name,size,file,folder,fileSystemInfo,parentReference,shared,createdBy,deleted";

    /// <summary>
    /// See <see cref="IDeltaSource"/>. A null <paramref name="deltaToken"/> means "enumerate the
    /// entire current tree"; a non-null one is itself a full URL (Graph's
    /// <c>@odata.nextLink</c>/<c>@odata.deltaLink</c> convention) and is used as-is.
    /// </summary>
    async Task<DeltaFetchResult> IDeltaSource.GetChangesAsync(string? deltaToken, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchDeltaAsync(deltaToken, wasFullResync: deltaToken is null, cancellationToken);
        }
        catch (DriveException ex) when (ex.ExitCode == (int)HttpStatusCode.Gone && deltaToken is not null)
        {
            // The stored cursor expired. Graph's documented behavior for a fresh delta call (token:
            // null) is to enumerate the entire current tree as adds, so this needs no separate
            // full-walk fallback path — just a retry with no token.
            return await FetchDeltaAsync(null, wasFullResync: true, cancellationToken);
        }
    }

    /// <summary>
    /// Hard ceiling on delta pages per call — never verified live before this shipped, so this is a
    /// safety net against a pagination bug (a page whose <c>@odata.nextLink</c> never resolves to a
    /// terminal <c>@odata.deltaLink</c>) hanging the sync scheduler forever instead of failing
    /// loudly. 5,000 pages at Graph's default page size is roughly a million items — far more than
    /// any legitimate single delta call should ever see.
    /// </summary>
    private const int MaxDeltaPages = 5000;

    private async Task<DeltaFetchResult> FetchDeltaAsync(string? deltaToken, bool wasFullResync, CancellationToken cancellationToken)
    {
        // $top explicit rather than trusting Graph's server-side default, same as ListFolderAsync
        // — a resumed call via a stored nextLink/deltaLink already encodes its own page size
        // verbatim, so this only matters for the very first page of a fresh/full-resync call.
        var url = deltaToken ?? $"{BaseUrl}/root/delta?$select={DeltaSelectFields}&$top=200";
        var changes = new List<DeltaChange>();
        string? nextToken = null;

        // Must be followed to exhaustion, same reasoning as ListFolderAsync's own paging loop: a
        // page carries either @odata.nextLink (more to fetch) or @odata.deltaLink (this was the
        // last page; its value is the cursor for the next call).
        for (var pageNumber = 1; ; pageNumber++)
        {
            if (pageNumber > MaxDeltaPages)
            {
                throw new DriveException(url, 0, string.Empty, string.Empty,
                    $"The OneDrive delta query did not finish after {MaxDeltaPages} pages — aborting rather than looping forever.",
                    DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpDeltaTooManyPages, "OneDrive", MaxDeltaPages) };
            }

            using var response = await _http.SendAsync($"GET /root/delta (page {pageNumber})", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var page = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GraphDeltaPage, cancellationToken)
                ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, "OneDrive returned an empty delta page.", DriveErrorKind.Unknown) { Detail = LocalizedText.Of(StringKeys.Error.OpEmptyDeltaPage, "OneDrive") };

            foreach (var item in page.Value)
            {
                changes.Add(ToDeltaChange(item));
            }

            if (page.NextLink is not null)
            {
                url = page.NextLink;
                continue;
            }

            nextToken = page.DeltaLink;
            break;
        }

        return new DeltaFetchResult(changes, nextToken, wasFullResync);
    }

    private DeltaChange ToDeltaChange(GraphDriveItem item)
    {
        var path = ResolvePathFromParentReference(item);

        if (item.Deleted is not null)
        {
            // A deleted item's DriveItem only needs enough for the caller to remove it by path —
            // size/hash/etc. are irrelevant since the item is gone.
            return new DeltaChange(new DriveItem(Path: path, Name: item.Name, IsFolder: item.Folder is not null, NodeId: item.Id), IsDeleted: true);
        }

        return new DeltaChange(BuildDriveItem(item, path), IsDeleted: false);
    }

    /// <summary>
    /// Delta items arrive in arbitrary tree order with no ambient parent path (unlike
    /// <see cref="ListFolderAsync"/>'s recursive walk, where the caller already knows it) — so the
    /// path has to be built from Graph's own <c>parentReference.path</c>:
    /// <c>/drive/root:/A/B</c>, URL-encoded segments. Strip the <c>root:</c> prefix, URL-decode
    /// each segment, then append the item's own name via the same path syntax the rest of this
    /// provider uses (docs/PLAN-CLOUD-PROVIDERS.md P8).
    /// </summary>
    private string ResolvePathFromParentReference(GraphDriveItem item)
    {
        const string marker = "root:";
        var rawParentPath = item.ParentReference?.Path;
        var afterMarker = rawParentPath is not null && rawParentPath.IndexOf(marker, StringComparison.Ordinal) is var idx && idx >= 0
            ? rawParentPath[(idx + marker.Length)..]
            : string.Empty;

        var decodedSegments = afterMarker
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString);
        var parentPath = string.Join('/', decodedSegments) is { Length: > 0 } joined ? $"/{joined}" : string.Empty;

        return _paths.Combine(parentPath, item.Name);
    }

    private static string ConflictBehaviorFor(UploadConflictStrategy strategy) => strategy switch
    {
        UploadConflictStrategy.Replace => "replace",
        UploadConflictStrategy.KeepBoth => "rename",
        _ => "fail", // None and Skip both map to "fail"; Skip's resulting 409 is translated to success by the caller.
    };

    /// <summary>Builds `.../root:/{encoded path}:` — root itself has no trailing colon segment.</summary>
    private static string ItemSegment(string path)
    {
        var trimmed = path.Trim('/');
        return trimmed.Length == 0
            ? $"{BaseUrl}/root"
            : $"{BaseUrl}/root:/{EncodePath(trimmed)}:";
    }

    /// <summary>Percent-encodes each path segment individually so a literal '/' in a name never gets mistaken for the path separator.</summary>
    private static string EncodePath(string trimmedPath)
        => string.Join('/', trimmedPath.Split('/').Select(Uri.EscapeDataString));

    private static string DescribePath(string path) => path.Length == 0 ? "/" : path;

    private static string PathName(string path) => path.TrimEnd('/').Split('/').Last();

    /// <summary>
    /// Maps a <see cref="GraphDriveItem"/> to <see cref="DriveItem"/> per docs/PLAN-CLOUD-PROVIDERS.md
    /// §4.4 — <see cref="DriveItem.Path"/> is built from <paramref name="parentPath"/> + the item's
    /// own name, not read from <c>parentReference.path</c> (which is URL-encoded and prefixed
    /// `/drive/root:`, not this app's path convention).
    /// </summary>
    private DriveItem ToDriveItem(GraphDriveItem item, string parentPath)
        => BuildDriveItem(item, _paths.Combine(parentPath, item.Name));

    private static DriveItem BuildDriveItem(GraphDriveItem item, string path)
    {
        var isFolder = item.Folder is not null;
        // Only quickXorHash, deliberately never falling back to sha1Hash/sha256Hash: this
        // provider's Capabilities.RemoteHash is the fixed value QuickXor (see OneDriveProvider), so
        // tagging a sha1-only item's hash as QuickXor would silently mislabel it — exactly the
        // "hash-algorithm mismatch is silent and destructive" risk P3's guard exists to prevent
        // (docs/PLAN-CLOUD-PROVIDERS.md R2). A file with no quickXorHash (unverified how common this
        // is on a personal drive — §4.4/O4) just gets no content hash here, which RemoteScanner
        // already handles by leaving RemoteHashAlgorithm null too — a safe degrade, not a mislabel.
        var contentHash = item.File?.Hashes?.QuickXorHash;
        return new DriveItem(
            Path: path,
            Name: item.Name,
            IsFolder: isFolder,
            Size: isFolder ? null : item.Size,
            ModifiedAt: item.FileSystemInfo?.LastModifiedDateTime,
            Owner: item.CreatedBy?.User?.Email ?? item.CreatedBy?.User?.DisplayName,
            IsShared: item.Shared is not null,
            NodeId: item.Id,
            ContentHash: contentHash);
    }
}
