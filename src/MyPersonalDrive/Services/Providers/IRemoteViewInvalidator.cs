namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Optional capability: discard whatever the backend has cached about the remote tree before a
/// scan. Present on <see cref="ICloudDriveProvider.RemoteView"/> only for providers whose listing
/// is served from a cache the backend never revalidates (Proton's CLI — see
/// docs/PLAN-LOCAL-SYNC.md Appendix A #16). A provider answering straight from the service
/// (Microsoft Graph) has nothing to invalidate and leaves this null.
/// </summary>
public interface IRemoteViewInvalidator
{
    Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default);
}
