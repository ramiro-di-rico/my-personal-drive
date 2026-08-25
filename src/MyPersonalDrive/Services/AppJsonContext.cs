using System.Text.Json.Serialization;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SyncActionPayload))]
[JsonSerializable(typeof(CliReleaseManifest))]
// The FolderMetrics.BucketsJson column (docs/PLAN-BROWSER-VIEWS.md M4). Native AOT: every
// serialized type has to be declared here, there is no reflection fallback.
[JsonSerializable(typeof(List<FolderKindBucket>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext
{
}
