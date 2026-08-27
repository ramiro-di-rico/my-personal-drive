namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>
/// Shared between <see cref="QuickXorHasherTests.KnownGoldenVector_MatchesGraphsRealQuickXorHash"/>
/// and the live integration test (<c>Integration/RealOneDriveAuthTests.cs</c>), which uploads this
/// exact content and is what originally confirmed <see cref="Content"/>'s hash against a real
/// Graph-reported <c>quickXorHash</c> (docs/PLAN-CLOUD-PROVIDERS.md Appendix A #3). 81 bytes —
/// long enough to exercise every wraparound-boundary byte index (14/29/43/58 mod 160 at
/// <c>Shift=11</c>) the original accumulator bug silently dropped.
/// </summary>
public static class QuickXorGoldenVector
{
    public const string Content = "MyPersonalDrive QuickXorHash golden vector (P6) — fixed content, do not change.";
}
