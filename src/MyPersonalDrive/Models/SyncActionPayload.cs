namespace MyPersonalDrive.Models;

/// <summary>
/// Serialized into <c>SyncQueue.Payload</c> as JSON to carry the <see cref="SyncAction"/>
/// fields that don't have their own column. A plain settable class (not a record) so
/// System.Text.Json's source generator (see <c>AppJsonContext</c>) has no ambiguity about
/// constructor binding — this app is Native AOT, so reflection-based serialization isn't
/// available as a fallback.
/// </summary>
public sealed class SyncActionPayload
{
    public string? SecondaryPath { get; set; }
    public long? Bytes { get; set; }
}
