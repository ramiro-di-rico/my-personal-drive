using System.Text.Json.Serialization;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers.GoogleDrive;
using MyPersonalDrive.Services.Providers.OneDrive;

namespace MyPersonalDrive.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SyncActionPayload))]
[JsonSerializable(typeof(CliReleaseManifest))]
// The FolderMetrics.BucketsJson column (docs/PLAN-BROWSER-VIEWS.md M4). Native AOT: every
// serialized type has to be declared here, there is no reflection fallback.
[JsonSerializable(typeof(List<FolderKindBucket>))]
// OneDrive/Graph (docs/PLAN-CLOUD-PROVIDERS.md P6).
[JsonSerializable(typeof(StoredOneDriveToken))]
[JsonSerializable(typeof(GraphTokenResponse))]
[JsonSerializable(typeof(GraphUser))]
[JsonSerializable(typeof(GraphItemsPage))]
[JsonSerializable(typeof(GraphDriveItem))]
[JsonSerializable(typeof(GraphDeltaPage))]
[JsonSerializable(typeof(GraphErrorEnvelope))]
[JsonSerializable(typeof(GraphCopyMonitorStatus))]
[JsonSerializable(typeof(GraphUploadSession))]
[JsonSerializable(typeof(GraphRenameRequest))]
[JsonSerializable(typeof(GraphMoveRequest))]
[JsonSerializable(typeof(GraphCopyRequest))]
[JsonSerializable(typeof(GraphCreateFolderRequest))]
[JsonSerializable(typeof(GraphCreateUploadSessionRequest))]
[JsonSerializable(typeof(GraphSharingLinkRequest))]
[JsonSerializable(typeof(GraphPermission))]
// Google Drive (docs/PLAN-CLOUD-PROVIDERS.md P10).
[JsonSerializable(typeof(StoredGoogleDriveToken))]
[JsonSerializable(typeof(GoogleDriveTokenResponse))]
[JsonSerializable(typeof(GoogleDriveAboutResponse))]
[JsonSerializable(typeof(GoogleDriveFilesPage))]
[JsonSerializable(typeof(GoogleDriveFile))]
[JsonSerializable(typeof(GoogleDriveErrorEnvelope))]
[JsonSerializable(typeof(GoogleDriveRenameRequest))]
[JsonSerializable(typeof(GoogleDriveTrashRequest))]
[JsonSerializable(typeof(GoogleDriveCreateFileRequest))]
[JsonSerializable(typeof(GoogleDriveCopyRequest))]
[JsonSerializable(typeof(GoogleDrivePermissionRequest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext
{
}
