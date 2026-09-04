using System.Text.Json.Serialization;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Typed request/response DTOs for the Drive v3 calls <see cref="GoogleDriveOperations"/> makes.
/// Kept as real types rather than anonymous objects — same AOT rule <c>OneDrive.GraphRequests.cs</c>
/// states: <c>JsonContent.Create</c> on an anonymous type falls back to reflection-based
/// serialization, which Native AOT can't do.
/// </summary>
public sealed class GoogleDriveFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("parents")]
    public List<string>? Parents { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("modifiedTime")]
    public DateTimeOffset? ModifiedTime { get; set; }

    [JsonPropertyName("md5Checksum")]
    public string? Md5Checksum { get; set; }

    [JsonPropertyName("sha256Checksum")]
    public string? Sha256Checksum { get; set; }

    [JsonPropertyName("trashed")]
    public bool? Trashed { get; set; }

    [JsonPropertyName("webViewLink")]
    public string? WebViewLink { get; set; }
}

/// <summary>One page of `files.list` — `files` plus, when more remain, `nextPageToken`. Must be followed to exhaustion, same "a partial listing reads as a remote deletion" rule as every other provider's paging.</summary>
public sealed class GoogleDriveFilesPage
{
    [JsonPropertyName("files")]
    public List<GoogleDriveFile> Files { get; set; } = [];

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

public sealed class GoogleDriveRenameRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class GoogleDriveTrashRequest
{
    [JsonPropertyName("trashed")]
    public bool Trashed { get; set; } = true;
}

/// <summary>Metadata part of a `files.create`/multipart-upload body — also used standalone for a folder create (no content part).</summary>
public sealed class GoogleDriveCreateFileRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parents")]
    public List<string> Parents { get; set; } = [];

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>Client-settable directly in the create/update body — no v2-style `setModifiedDate` flag needed (docs/PLAN-CLOUD-PROVIDERS.md §8.5/G5).</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTimeOffset? ModifiedTime { get; set; }
}

public sealed class GoogleDriveCopyRequest
{
    [JsonPropertyName("parents")]
    public List<string> Parents { get; set; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Body of `POST .../permissions` — "reader"/"anyone" is the least-privileged combination Drive offers, same choice as OneDrive's "view"/"anonymous".</summary>
public sealed class GoogleDrivePermissionRequest
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "reader";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "anyone";
}

/// <summary>The `drive/v3/about?fields=user` response — just enough to build the settings card's account label.</summary>
public sealed class GoogleDriveAboutResponse
{
    [JsonPropertyName("user")]
    public GoogleDriveAboutUser? User { get; set; }
}

public sealed class GoogleDriveAboutUser
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }
}

/// <summary>The `{"error":{...}}` body Drive returns on every non-2xx response — v3 shape, distinct from v2 (docs/PLAN-CLOUD-PROVIDERS.md §8.7).</summary>
public sealed class GoogleDriveErrorEnvelope
{
    [JsonPropertyName("error")]
    public GoogleDriveErrorDetail? Error { get; set; }
}

public sealed class GoogleDriveErrorDetail
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errors")]
    public List<GoogleDriveErrorItem>? Errors { get; set; }
}

public sealed class GoogleDriveErrorItem
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }
}
