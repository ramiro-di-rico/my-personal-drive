using System.Net.Http.Json;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// <see cref="IDriveOperations"/> over the Drive v3 REST API. The one real structural departure
/// from <c>OneDrive.OneDriveOperations</c>: Drive has no native path at all, so every operation
/// resolves its path-string argument to an internal id first, via <see cref="ResolveIdAsync"/>
/// (docs/PLAN-CLOUD-PROVIDERS.md §8.2/G2). Everything else — listing/pagination, upload/download,
/// trash/rename/move/copy, share links, error handling — follows the same per-request shape
/// <see cref="GoogleDriveHttpClient"/> and <see cref="GoogleDriveOperations"/>'s OneDrive
/// counterpart already establish.
/// </summary>
public sealed class GoogleDriveOperations : IDriveOperations
{
    private const string RootId = "root";
    private const string GoogleNativeMimePrefix = "application/vnd.google-apps.";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string ListFields = "nextPageToken,files(id,name,mimeType,parents,size,modifiedTime,md5Checksum,sha256Checksum,trashed)";
    private const string LookupFields = "files(id,name)";

    /// <summary>Drive's own single-request (multipart) upload ceiling; larger files go through a resumable upload session.</summary>
    private const long SmallUploadCeilingBytes = 5L * 1024 * 1024;

    /// <summary>A clean multiple of Drive's required 256 KiB chunk unit (docs/PLAN-CLOUD-PROVIDERS.md §8.6).</summary>
    private const int UploadChunkSizeBytes = 8 * 256 * 1024; // 2,097,152 bytes = 2 MiB

    private readonly GoogleDriveHttpClient _http;
    private readonly GoogleDrivePathSyntax _paths = new();

    /// <summary>
    /// Path→id cache, seeded with the well-known root alias. Scoped to this instance's lifetime —
    /// a full <see cref="ListFolderAsync"/> walk populates it cheaply as a side effect (every child
    /// it lists is cached under its own path), so a targeted single-path call like
    /// <see cref="DownloadFileAsync"/> only pays the segment-walk cost directly when nothing has
    /// visited that path yet. Ordinal: Drive paths are case-sensitive (<see cref="GoogleDrivePathSyntax.Comparison"/>).
    /// </summary>
    private readonly Dictionary<string, string> _idCache = new(StringComparer.Ordinal) { ["/"] = RootId };

    public GoogleDriveOperations(GoogleDriveHttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedParentPath = NormalizePath(path);
        var parentId = await ResolveIdAsync(path, cancellationToken);
        var items = new List<DriveItem>();
        string? pageToken = null;

        // Must be followed to exhaustion: a partial listing reads as a remote deletion to the sync
        // reconciler, same failure mode every other provider's own paging loop already guards
        // against (docs/PLAN-CLOUD-PROVIDERS.md §8.3).
        do
        {
            var url = BuildListUrl(parentId, nameFilter: null, pageToken, ListFields);
            using var response = await _http.SendAsync($"GET {DescribePath(path)}/children", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var page = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFilesPage, cancellationToken)
                ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, "Google Drive devolvió una página de listado vacía.", DriveErrorKind.Unknown);

            foreach (var file in page.Files)
            {
                var childPath = _paths.Combine(normalizedParentPath, file.Name);
                _idCache[childPath] = file.Id;
                items.Add(ToDriveItem(file, childPath));
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return items;
    }

    public async Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(path, cancellationToken);
        var name = PathName(path);
        var url = $"{GoogleDriveHttpClient.BaseUrl}files/{id}?alt=media";
        using var response = await _http.SendAsync($"GET {DescribePath(path)}?alt=media", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        Directory.CreateDirectory(localFolder);
        var localPath = Path.Combine(localFolder, name);
        await using var target = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await response.Content.CopyToAsync(target, cancellationToken);
    }

    public async Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default)
    {
        var parentId = await ResolveIdAsync(parentPath, cancellationToken);
        var normalizedParentPath = NormalizePath(parentPath);

        foreach (var localPath in localPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadOneAsync(localPath, parentId, normalizedParentPath, strategy, cancellationToken);
        }
    }

    private async Task UploadOneAsync(string localPath, string parentId, string normalizedParentPath, UploadConflictStrategy strategy, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(localPath);

        // Drive never rejects a duplicate name server-side — the conflict has to be enforced
        // client-side by listing the target folder for an exact-name match first
        // (docs/PLAN-CLOUD-PROVIDERS.md §8.6/R7). Stated plainly: this check-then-act has a race if
        // another Drive client creates a same-named file between this list and the eventual create
        // — a known, accepted limitation (§6/R8), not something this pass attempts to solve.
        var existing = await FindExistingByExactNameAsync(parentId, name, cancellationToken);

        if (existing is null)
        {
            var newFile = await CreateAndUploadAsync(localPath, parentId, name, existingId: null, cancellationToken);
            _idCache[_paths.Combine(normalizedParentPath, newFile.Name)] = newFile.Id;
            return;
        }

        switch (strategy)
        {
            case UploadConflictStrategy.Skip:
                // Matches Proton's/OneDrive's own Skip semantics: a same-named target silently
                // succeeds with no upload attempted.
                return;

            case UploadConflictStrategy.Replace:
                var replaced = await CreateAndUploadAsync(localPath, parentId, name, existingId: existing.Id, cancellationToken);
                _idCache[_paths.Combine(normalizedParentPath, replaced.Name)] = replaced.Id;
                return;

            case UploadConflictStrategy.KeepBoth:
                var keepBothName = await ReserveKeepBothNameAsync(parentId, name, cancellationToken);
                var kept = await CreateAndUploadAsync(localPath, parentId, keepBothName, existingId: null, cancellationToken);
                _idCache[_paths.Combine(normalizedParentPath, kept.Name)] = kept.Id;
                return;

            case UploadConflictStrategy.None:
            default:
                throw new DriveException($"POST files (create {name})", 0, string.Empty, string.Empty,
                    $"'{name}' already exists in this folder on Google Drive.", DriveErrorKind.AlreadyExists);
        }
    }

    /// <summary>Appends a numbered suffix (" (2)", " (3)", …) until a name with no existing sibling is found — Drive has no server-side rename-on-conflict the way Graph's "rename" conflict behavior offers.</summary>
    private async Task<string> ReserveKeepBothNameAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        var dot = name.LastIndexOf('.');
        var baseName = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        for (var attempt = 2; attempt < 1000; attempt++)
        {
            var candidate = $"{baseName} ({attempt}){extension}";
            if (await FindExistingByExactNameAsync(parentId, candidate, cancellationToken) is null)
            {
                return candidate;
            }
        }

        throw new DriveException($"POST files (create {name})", 0, string.Empty, string.Empty,
            $"No se encontró un nombre libre para '{name}' en Google Drive después de 1000 intentos.", DriveErrorKind.AlreadyExists);
    }

    private async Task<GoogleDriveFile> CreateAndUploadAsync(string localPath, string parentId, string name, string? existingId, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(localPath);
        return fileInfo.Length <= SmallUploadCeilingBytes
            ? await UploadMultipartAsync(localPath, fileInfo, parentId, name, existingId, cancellationToken)
            : await UploadResumableAsync(localPath, fileInfo, parentId, name, existingId, cancellationToken);
    }

    private async Task<GoogleDriveFile> UploadMultipartAsync(string localPath, FileInfo fileInfo, string parentId, string name, string? existingId, CancellationToken cancellationToken)
    {
        var url = existingId is null
            ? $"{GoogleDriveHttpClient.UploadBaseUrl}files?uploadType=multipart"
            : $"{GoogleDriveHttpClient.UploadBaseUrl}files/{existingId}?uploadType=multipart";
        var method = existingId is null ? HttpMethod.Post : HttpMethod.Patch;
        var metadata = BuildMetadata(name, parentId, fileInfo, includeParents: existingId is null);

        using var response = await _http.SendAsync(
            $"{method} files (multipart {name})",
            () =>
            {
                var multipart = new MultipartContent("related", boundary: Guid.NewGuid().ToString("N"));
                multipart.Add(JsonContent.Create(metadata, AppJsonContext.Default.GoogleDriveCreateFileRequest));
                var media = new StreamContent(File.OpenRead(localPath));
                media.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(media);
                return new HttpRequestMessage(method, url) { Content = multipart };
            },
            cancellationToken);

        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken)
            ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió el archivo subido para {name}.", DriveErrorKind.Unknown);
    }

    private async Task<GoogleDriveFile> UploadResumableAsync(string localPath, FileInfo fileInfo, string parentId, string name, string? existingId, CancellationToken cancellationToken)
    {
        var initiateUrl = existingId is null
            ? $"{GoogleDriveHttpClient.UploadBaseUrl}files?uploadType=resumable"
            : $"{GoogleDriveHttpClient.UploadBaseUrl}files/{existingId}?uploadType=resumable";
        var initiateMethod = existingId is null ? HttpMethod.Post : HttpMethod.Patch;
        var metadata = BuildMetadata(name, parentId, fileInfo, includeParents: existingId is null);

        using var initiateResponse = await _http.SendAsync(
            $"{initiateMethod} files (resumable-initiate {name})",
            () => new HttpRequestMessage(initiateMethod, initiateUrl)
            {
                Content = JsonContent.Create(metadata, AppJsonContext.Default.GoogleDriveCreateFileRequest),
            },
            cancellationToken);
        var sessionUri = initiateResponse.Headers.Location?.ToString()
            ?? throw new DriveException(initiateUrl, (int)initiateResponse.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió una sesión de subida reanudable para {name}.", DriveErrorKind.Unknown);
        initiateResponse.Content.Dispose();

        await using var stream = File.OpenRead(localPath);
        var totalLength = stream.Length;
        var buffer = new byte[UploadChunkSizeBytes];
        long offset = 0;
        HttpResponseMessage? lastResponse = null;

        try
        {
            while (offset < totalLength)
            {
                var chunkLength = (int)Math.Min(UploadChunkSizeBytes, totalLength - offset);
                var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, chunkLength), chunkLength, throwOnEndOfStream: false, cancellationToken);
                var rangeEnd = offset + read - 1;

                using var chunkContent = new ByteArrayContent(buffer, 0, read);
                chunkContent.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, rangeEnd, totalLength);
                // The session URI is pre-authenticated per Drive's resumable-upload contract and
                // must NOT carry the bearer header, same reasoning as OneDrive's own chunked upload.
                lastResponse?.Dispose();
                lastResponse = await _http.SendUnauthenticatedAsync(
                    new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = chunkContent },
                    cancellationToken);

                if (!lastResponse.IsSuccessStatusCode)
                {
                    var body = await lastResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw new DriveException(sessionUri, (int)lastResponse.StatusCode, string.Empty, body,
                        $"La subida de {name} a Google Drive falló en el byte {offset}.", GoogleDriveErrorClassifier.Classify(lastResponse.StatusCode, body));
                }

                offset += read;
            }

            return lastResponse is not null
                ? await lastResponse.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken)
                    ?? throw new DriveException(sessionUri, 0, string.Empty, string.Empty, $"Google Drive no devolvió el archivo subido para {name}.", DriveErrorKind.Unknown)
                : throw new DriveException(sessionUri, 0, string.Empty, string.Empty, $"'{name}' estaba vacío; nunca se subió ningún fragmento.", DriveErrorKind.Unknown);
        }
        finally
        {
            lastResponse?.Dispose();
        }
    }

    private static GoogleDriveCreateFileRequest BuildMetadata(string name, string parentId, FileInfo fileInfo, bool includeParents)
        => new()
        {
            Name = name,
            Parents = includeParents ? [parentId] : [],
            ModifiedTime = fileInfo.LastWriteTimeUtc,
        };

    private async Task<GoogleDriveFile?> FindExistingByExactNameAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        var url = BuildListUrl(parentId, name, pageToken: null, LookupFields);
        using var response = await _http.SendAsync($"GET files (lookup {name})", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var page = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFilesPage, cancellationToken);
        // First match in listing order wins deterministically — Drive's files.list order is not
        // itself guaranteed stable across calls (unverified, docs/PLAN-CLOUD-PROVIDERS.md §8.2),
        // an accepted limitation shared with the sync-side duplicate-name handling.
        return page?.Files.FirstOrDefault();
    }

    public async Task TrashItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(path, cancellationToken);
        var url = $"{GoogleDriveHttpClient.BaseUrl}files/{id}";
        using var response = await _http.SendAsync(
            $"PATCH {DescribePath(path)} (trash)",
            () => new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(new GoogleDriveTrashRequest(), AppJsonContext.Default.GoogleDriveTrashRequest) },
            cancellationToken);
        response.Content.Dispose();
        _idCache.Remove(NormalizePath(path));
    }

    public async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(path, cancellationToken);
        var url = $"{GoogleDriveHttpClient.BaseUrl}files/{id}";
        using var response = await _http.SendAsync(
            $"PATCH {DescribePath(path)} (rename)",
            () => new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(new GoogleDriveRenameRequest { Name = newName }, AppJsonContext.Default.GoogleDriveRenameRequest) },
            cancellationToken);
        response.Content.Dispose();

        var normalized = NormalizePath(path);
        _idCache.Remove(normalized);
        _idCache[_paths.Combine(ParentPathOf(normalized), newName)] = id;
    }

    public async Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var parentId = await ResolveIdAsync(parentPath, cancellationToken);
        var url = $"{GoogleDriveHttpClient.BaseUrl}files";
        var body = new GoogleDriveCreateFileRequest { Name = name, MimeType = FolderMimeType, Parents = [parentId] };

        using var response = await _http.SendAsync(
            $"POST {DescribePath(parentPath)}/children (folder)",
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, AppJsonContext.Default.GoogleDriveCreateFileRequest) },
            cancellationToken);
        var created = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken)
            ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió la carpeta creada {name}.", DriveErrorKind.Unknown);

        _idCache[_paths.Combine(NormalizePath(parentPath), name)] = created.Id;
    }

    public async Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default)
    {
        var targetId = await ResolveIdAsync(targetParentPath, cancellationToken);
        var normalizedTargetPath = NormalizePath(targetParentPath);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = await ResolveIdAsync(path, cancellationToken);
            var currentParentId = await GetCurrentParentIdAsync(id, cancellationToken);
            var url = $"{GoogleDriveHttpClient.BaseUrl}files/{id}?addParents={Uri.EscapeDataString(targetId)}&removeParents={Uri.EscapeDataString(currentParentId)}";

            using var response = await _http.SendAsync(
                $"PATCH {DescribePath(path)} (move)",
                () => new HttpRequestMessage(HttpMethod.Patch, url),
                cancellationToken);
            response.Content.Dispose();

            var normalized = NormalizePath(path);
            _idCache.Remove(normalized);
            _idCache[_paths.Combine(normalizedTargetPath, PathName(path))] = id;
        }
    }

    public async Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        var sourceId = await ResolveIdAsync(sourcePath, cancellationToken);
        var targetId = await ResolveIdAsync(targetParentPath, cancellationToken);
        var url = $"{GoogleDriveHttpClient.BaseUrl}files/{sourceId}/copy";
        var body = new GoogleDriveCopyRequest { Parents = [targetId], Name = newName };

        // Drive's copy completes synchronously — no monitor-URL/polling dance the way Graph's
        // async 202 response needs (Capabilities.CopyIsAsynchronous = false, docs/PLAN-CLOUD-PROVIDERS.md §8.6).
        using var response = await _http.SendAsync(
            $"POST {DescribePath(sourcePath)}/copy",
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, AppJsonContext.Default.GoogleDriveCopyRequest) },
            cancellationToken);
        var copied = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken)
            ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió la copia de {sourcePath}.", DriveErrorKind.Unknown);

        _idCache[_paths.Combine(NormalizePath(targetParentPath), copied.Name)] = copied.Id;
    }

    public async Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(path, cancellationToken);
        var permissionsUrl = $"{GoogleDriveHttpClient.BaseUrl}files/{id}/permissions";
        using var permissionResponse = await _http.SendAsync(
            $"POST {DescribePath(path)}/permissions",
            () => new HttpRequestMessage(HttpMethod.Post, permissionsUrl) { Content = JsonContent.Create(new GoogleDrivePermissionRequest(), AppJsonContext.Default.GoogleDrivePermissionRequest) },
            cancellationToken);
        permissionResponse.Content.Dispose();

        var fileUrl = $"{GoogleDriveHttpClient.BaseUrl}files/{id}?fields=webViewLink";
        using var fileResponse = await _http.SendAsync($"GET {DescribePath(path)}?fields=webViewLink", () => new HttpRequestMessage(HttpMethod.Get, fileUrl), cancellationToken);
        var file = await fileResponse.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken);

        return file?.WebViewLink is { Length: > 0 } webViewLink
            ? webViewLink
            : throw new DriveException(fileUrl, (int)fileResponse.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió un enlace para compartir de {path}.", DriveErrorKind.Unknown);
    }

    private async Task<string> GetCurrentParentIdAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"{GoogleDriveHttpClient.BaseUrl}files/{id}?fields=parents";
        using var response = await _http.SendAsync($"GET files/{id} (parents)", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var file = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFile, cancellationToken);
        // Drive is treated as effectively single-parent throughout this provider
        // (docs/PLAN-CLOUD-PROVIDERS.md §8.2) — the v3 File resource's own docs state at most one
        // parent per file.
        return file?.Parents is { Count: > 0 } parents
            ? parents[0]
            : throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"Google Drive no devolvió un padre para el elemento {id}.", DriveErrorKind.Unknown);
    }

    /// <summary>
    /// Resolves a sync-style path (<c>"/"</c>-separated, this app's own convention) to a Drive id,
    /// walking one segment at a time from the well-known <c>root</c> alias and caching every
    /// intermediate id under its own full path (docs/PLAN-CLOUD-PROVIDERS.md §8.2/G2).
    /// </summary>
    private async Task<string> ResolveIdAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        if (_idCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "/";
        var currentId = RootId;

        foreach (var segment in segments)
        {
            var childPath = _paths.Combine(currentPath, segment);
            if (_idCache.TryGetValue(childPath, out var cachedChildId))
            {
                currentId = cachedChildId;
                currentPath = childPath;
                continue;
            }

            var url = BuildListUrl(currentId, segment, pageToken: null, LookupFields);
            using var response = await _http.SendAsync($"GET files (resolve {childPath})", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var page = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GoogleDriveFilesPage, cancellationToken);
            // First match wins deterministically when the parent holds duplicate-named siblings —
            // same accepted, documented limitation as FindExistingByExactNameAsync above.
            var match = page?.Files.FirstOrDefault()
                ?? throw new DriveException(url, (int)response.StatusCode, string.Empty, string.Empty, $"No se encontró '{segment}' en Google Drive dentro de {currentPath}.", DriveErrorKind.NotFound);

            _idCache[childPath] = match.Id;
            currentId = match.Id;
            currentPath = childPath;
        }

        return currentId;
    }

    private static string BuildListUrl(string parentId, string? nameFilter, string? pageToken, string fields)
    {
        var q = nameFilter is null
            ? $"'{EscapeQueryValue(parentId)}' in parents and trashed=false"
            : $"'{EscapeQueryValue(parentId)}' in parents and name='{EscapeQueryValue(nameFilter)}' and trashed=false";
        var url = $"{GoogleDriveHttpClient.BaseUrl}files?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString(fields)}&pageSize=1000&spaces=drive&corpora=user";
        return pageToken is null ? url : $"{url}&pageToken={Uri.EscapeDataString(pageToken)}";
    }

    /// <summary>Drive's query-string escaping rule: a literal <c>'</c> becomes <c>\'</c> and a literal <c>\</c> becomes <c>\\</c> (docs/PLAN-CLOUD-PROVIDERS.md §8.2).</summary>
    private static string EscapeQueryValue(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim('/');
        return trimmed.Length == 0 ? "/" : $"/{trimmed}";
    }

    private static string ParentPathOf(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalizedPath[..lastSlash];
    }

    private static string DescribePath(string path) => path.Length == 0 ? "/" : path;

    private static string PathName(string path) => path.TrimEnd('/').Split('/').Last();

    private static bool IsGoogleNativeFile(string? mimeType)
        => mimeType is not null && mimeType.StartsWith(GoogleNativeMimePrefix, StringComparison.Ordinal) && mimeType != FolderMimeType;

    /// <summary>
    /// Maps a <see cref="GoogleDriveFile"/> to <see cref="DriveItem"/> per docs/PLAN-CLOUD-PROVIDERS.md
    /// §8.4/G4. A Google-native file (Docs/Sheets/Slides/...) has no binary content at all — no
    /// checksum, and <see cref="DriveItem.IsRemoteOnlyDocument"/> is set so
    /// <see cref="Sync.RemoteScanner"/> skips it rather than attempting to sync it.
    /// </summary>
    private static DriveItem ToDriveItem(GoogleDriveFile file, string path)
    {
        var isFolder = file.MimeType == FolderMimeType;
        var isNative = IsGoogleNativeFile(file.MimeType);
        // Only sha256Checksum, deliberately never falling back to md5Checksum: this provider's
        // Capabilities.RemoteHash is the fixed value Sha256 (see GoogleDriveProvider), so tagging
        // an md5-only item's hash as Sha256 would silently mislabel it — exactly the
        // "hash-algorithm mismatch is silent and destructive" risk P3's guard exists to prevent
        // (docs/PLAN-CLOUD-PROVIDERS.md R2), and the same no-fallback rule
        // OneDriveOperations.BuildDriveItem already follows for quickXorHash. A file with no
        // sha256Checksum (unverified how common this is in practice — §8.4/G4) just gets no
        // content hash here, which RemoteScanner already handles as a safe degrade, not a mislabel.
        var contentHash = isNative ? null : file.Sha256Checksum;

        return new DriveItem(
            Path: path,
            Name: file.Name,
            IsFolder: isFolder,
            Size: isFolder ? null : file.Size,
            ModifiedAt: file.ModifiedTime,
            Owner: null,
            IsShared: false,
            NodeId: file.Id,
            ContentHash: contentHash,
            IsRemoteOnlyDocument: isNative);
    }
}
