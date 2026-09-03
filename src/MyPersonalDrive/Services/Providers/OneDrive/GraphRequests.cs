using System.Text.Json.Serialization;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Request bodies for the Graph calls that write. Kept as real types rather than anonymous
/// objects: <c>JsonContent.Create</c> on an anonymous type falls back to reflection-based
/// serialization, which Native AOT (<c>PublishAot=true</c> in the app's csproj) can't do — every
/// serialized type needs a source-generated <c>JsonTypeInfo</c> from <see cref="AppJsonContext"/>,
/// same rule as every other DTO in this app.
/// </summary>
public sealed class GraphRenameRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class GraphParentReferenceRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class GraphMoveRequest
{
    [JsonPropertyName("parentReference")]
    public GraphParentReferenceRequest ParentReference { get; set; } = new();
}

public sealed class GraphCopyRequest
{
    [JsonPropertyName("parentReference")]
    public GraphParentReferenceRequest ParentReference { get; set; } = new();

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>An empty object marks the create as a folder — Graph's own convention, mirroring <see cref="GraphFolderFacet"/> on the read side.</summary>
public sealed class GraphFolderCreationFacet
{
}

public sealed class GraphCreateFolderRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("folder")]
    public GraphFolderCreationFacet Folder { get; set; } = new();

    [JsonPropertyName("@microsoft.graph.conflictBehavior")]
    public string ConflictBehavior { get; set; } = "fail";
}

public sealed class GraphUploadSessionItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("@microsoft.graph.conflictBehavior")]
    public string ConflictBehavior { get; set; } = "fail";
}

public sealed class GraphCreateUploadSessionRequest
{
    [JsonPropertyName("item")]
    public GraphUploadSessionItem Item { get; set; } = new();
}

/// <summary>
/// Body for `POST .../createLink`. "view" (read-only) + "anonymous" (anyone with the link, no
/// sign-in) — the least-privileged combination Graph offers, matching this app's "copy path"
/// counterpart on Proton: a link to look at the item, not to edit it or limit it to the
/// organization's own directory (which a personal Microsoft account has none of).
/// </summary>
public sealed class GraphSharingLinkRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "view";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "anonymous";
}
