namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// A hand-rolled controllable clock. Deliberately not `Microsoft.Extensions.TimeProvider.Testing`:
/// the only thing needed here is "read the time, and let the test move it", which is a handful of
/// lines, and this repo keeps its dependency surface small.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
