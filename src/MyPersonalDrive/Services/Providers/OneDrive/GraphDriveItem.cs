using System.Text.Json.Serialization;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// One page of `GET .../children` — `value` plus, when the folder has more children than `$top`,
/// `@odata.nextLink` pointing at the next page. Must be followed to exhaustion: stopping early
/// reads as a remote deletion to the sync reconciler. Shape per Microsoft's public Graph docs
/// (learn.microsoft.com/graph/api/driveitem-list-children); pending a live-capture confirmation in
/// docs/PLAN-CLOUD-PROVIDERS.md Appendix A (default `$top` page size is marked unverified there).
/// </summary>
public sealed class GraphItemsPage
{
    [JsonPropertyName("value")]
    public List<GraphDriveItem> Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// A `driveItem` resource, trimmed to the fields the app's <c>$select</c> asks for — see
/// <see cref="OneDriveOperations"/>'s query string. Mapped to <c>Models.DriveItem</c> by
/// <see cref="OneDriveOperations.ToDriveItem"/>; the field-by-field provenance is documented there,
/// matching docs/PLAN-CLOUD-PROVIDERS.md §4.4.
/// </summary>
public sealed class GraphDriveItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("file")]
    public GraphFileFacet? File { get; set; }

    [JsonPropertyName("folder")]
    public GraphFolderFacet? Folder { get; set; }

    [JsonPropertyName("fileSystemInfo")]
    public GraphFileSystemInfo? FileSystemInfo { get; set; }

    [JsonPropertyName("createdBy")]
    public GraphIdentitySet? CreatedBy { get; set; }

    [JsonPropertyName("shared")]
    public object? Shared { get; set; }

    /// <summary>Present only on the top-level response of an upload/rename/create call, not on a listing entry.</summary>
    [JsonPropertyName("parentReference")]
    public GraphParentReference? ParentReference { get; set; }
}

public sealed class GraphFileFacet
{
    [JsonPropertyName("hashes")]
    public GraphHashes? Hashes { get; set; }
}

/// <summary>An empty object when present — its presence, not its content, means "this is a folder".</summary>
public sealed class GraphFolderFacet
{
    [JsonPropertyName("childCount")]
    public int? ChildCount { get; set; }
}

public sealed class GraphFileSystemInfo
{
    /// <summary>
    /// The client-claimed modification time — the true analogue of Proton's
    /// <c>claimedModificationTime</c>. Deliberately not the top-level <c>lastModifiedDateTime</c>,
    /// which is server-side (docs/PLAN-CLOUD-PROVIDERS.md §4.4).
    /// </summary>
    [JsonPropertyName("lastModifiedDateTime")]
    public DateTimeOffset? LastModifiedDateTime { get; set; }
}

public sealed class GraphHashes
{
    [JsonPropertyName("quickXorHash")]
    public string? QuickXorHash { get; set; }

    [JsonPropertyName("sha1Hash")]
    public string? Sha1Hash { get; set; }

    [JsonPropertyName("sha256Hash")]
    public string? Sha256Hash { get; set; }
}

public sealed class GraphIdentitySet
{
    [JsonPropertyName("user")]
    public GraphIdentity? User { get; set; }
}

public sealed class GraphIdentity
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed class GraphParentReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>The `/me` response — just enough to build the settings card's account label.</summary>
public sealed class GraphUser
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("mail")]
    public string? Mail { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

/// <summary>The `{"error":{...}}` body Graph returns on every non-2xx response.</summary>
public sealed class GraphErrorEnvelope
{
    [JsonPropertyName("error")]
    public GraphErrorDetail? Error { get; set; }
}

public sealed class GraphErrorDetail
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>The `202 Accepted` async-copy response's polling target — see docs/PLAN-CLOUD-PROVIDERS.md §4.3.</summary>
public sealed class GraphCopyMonitorStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("percentageComplete")]
    public double? PercentageComplete { get; set; }
}

/// <summary>Body of `POST .../createUploadSession` — where to `PUT` the chunks.</summary>
public sealed class GraphUploadSession
{
    [JsonPropertyName("uploadUrl")]
    public string UploadUrl { get; set; } = string.Empty;
}
