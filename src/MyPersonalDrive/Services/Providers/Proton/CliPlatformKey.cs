using System.Runtime.InteropServices;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Maps the running machine to the <c>Platform</c> key Proton's release manifest uses
/// (<c>linux/x64</c>, <c>linux/x64-musl</c>, <c>macos/arm64</c>, <c>windows/x64</c>, …).
///
/// The musl variants matter: a glibc build will not run on a musl-only distro, and picking the
/// wrong one produces a binary that fails at exec time rather than at download time. The only
/// signal available without probing the filesystem is the runtime identifier, which .NET already
/// resolves for the running process.
/// </summary>
internal static class CliPlatformKey
{
    /// <summary>Returns null when this OS/architecture combination has no manifest key.</summary>
    public static string? ForCurrentMachine()
        => Resolve(RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture);

    /// <param name="runtimeIdentifier">
    /// Used only to detect musl (e.g. <c>linux-musl-x64</c>); the OS itself comes from
    /// <see cref="OperatingSystem"/> so this stays correct even for an unfamiliar RID.
    /// </param>
    internal static string? Resolve(string runtimeIdentifier, Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        if (arch is null)
        {
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            var musl = runtimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase);
            return musl ? $"linux/{arch}-musl" : $"linux/{arch}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"macos/{arch}";
        }

        if (OperatingSystem.IsWindows())
        {
            return $"windows/{arch}";
        }

        return null;
    }
}
