using Xunit;

namespace MyPersonalDrive.Tests;

/// <summary>
/// A test whose probe relies on POSIX shell. Same conditional-skip pattern as
/// <see cref="Integration.IntegrationFactAttribute"/>, since xUnit 2.x has no runtime skip.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The probe script is POSIX shell.";
        }
    }
}
