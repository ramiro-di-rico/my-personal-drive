using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10 * 1024, "10 KB")]
    [InlineData(6196055, "5.9 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void Format_UsesBinaryStepsAndOneDecimalBelowTen(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Format_DoesNotRunOutOfUnits()
        => Assert.EndsWith("PB", ByteSize.Format(long.MaxValue));

    [Fact]
    public void Format_WithANegativeValue_DegradesInsteadOfThrowing()
        => Assert.Equal("-5 B", ByteSize.Format(-5));
}
