namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// One entry in a provider's activity feed — replaces the three separate
/// <c>CommandStarted</c>/<c>CommandOutput</c>/<c>CommandFinished</c> events so the UI console
/// works the same way for a process-based provider (Proton's CLI) and a request-based one
/// (Microsoft Graph). See docs/PLAN-CLOUD-PROVIDERS.md §2.6.
/// </summary>
public enum ActivityKind
{
    Started,
    Output,
    Finished
}

/// <summary>
/// <paramref name="Label"/> is the command line for Proton (<c>filesystem list "/my-files"</c>)
/// or the request description for a future HTTP-based provider
/// (<c>GET /me/drive/root:/Photos:/children</c>) — populated on <see cref="ActivityKind.Started"/>
/// and <see cref="ActivityKind.Finished"/>; null on <see cref="ActivityKind.Output"/>, since
/// Proton's line-by-line stdout carries no per-line command identifier to attribute it to
/// (pre-existing: concurrent read commands already interleave their output today).
/// <paramref name="ExitCode"/> stays nullable rather than being synthesized from an HTTP status,
/// so the console never invents a number that didn't come from the transport.
/// </summary>
public sealed record ProviderActivity(
    ActivityKind Kind,
    string? Label,
    string? Text,
    bool IsError,
    int? ExitCode,
    TimeSpan? Duration);
