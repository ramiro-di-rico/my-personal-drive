using System.Text.Json.Serialization;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SyncActionPayload))]
[JsonSerializable(typeof(CliReleaseManifest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext
{
}
