using MyPersonalDrive.ViewModels;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The console's text is what the UI thread has to shape on every update, so its size is a
/// performance contract, not a cosmetic detail. A captured stack during a 30-second hang was
/// <c>TextBlock.MeasureOverride</c> → <c>TextLayout.CreateTextLines</c> →
/// <c>HarfBuzzTextShaper.ShapeText</c>, with ~300 KB of text in one wrapping block: 200 retained
/// lines of `filesystem list --json`, each ~1520 characters.
/// </summary>
public class CommandLogBufferTests
{
    [Fact]
    public void ALongLine_IsTruncated_SoOneListingRowCannotDominateTheLayout()
    {
        // 1520 chars is the real measured length of one JSON row from the CLI.
        var sut = new CommandLogBuffer();
        sut.Add(new string('x', 1520));

        var rendered = sut.Render();
        Assert.True(rendered.Length < 1520, $"line kept its full length ({rendered.Length} chars)");
        Assert.Contains("truncado", rendered);
    }

    [Fact]
    public void AShortLine_IsLeftExactlyAsItIs()
    {
        // Commands, exit codes and error messages are the console's actual purpose; they must not
        // acquire a truncation marker.
        var sut = new CommandLogBuffer();
        sut.Add("> proton-drive filesystem list /my-files --json");

        Assert.Equal("> proton-drive filesystem list /my-files --json", sut.Render());
    }

    [Fact]
    public void TheWholeBuffer_StaysBounded_EvenUnderAFloodOfLongLines()
    {
        // The pathological case: eight concurrent CLI processes streaming listings. Whatever arrives,
        // the text handed to the layout has a hard ceiling.
        var sut = new CommandLogBuffer();
        for (var i = 0; i < 5_000; i++)
        {
            sut.Add(new string('y', 1520));
        }

        Assert.Equal(CommandLogBuffer.MaxLines, sut.Count);
        var ceiling = CommandLogBuffer.MaxLines * (CommandLogBuffer.MaxLineLength + 32);
        Assert.True(sut.Render().Length < ceiling,
            $"rendered {sut.Render().Length} chars, above the {ceiling} ceiling");
    }

    [Fact]
    public void TheOldestLinesAreDropped_NotTheNewest()
    {
        // A console that discarded the newest lines would hide the error you are looking at.
        var sut = new CommandLogBuffer(maxLines: 3);
        sut.AddRange(["one", "two", "three", "four"]);

        Assert.Equal(["two", "three", "four"], sut.Lines);
    }

    [Fact]
    public void ABatchIsEquivalentToTheSameLinesOneByOne()
    {
        // The view model switched from per-line appends to batched flushes; the two must agree, or
        // the console's contents would depend on CLI output timing.
        var oneByOne = new CommandLogBuffer(maxLines: 3);
        foreach (var line in new[] { "a", "b", "c", "d" })
        {
            oneByOne.Add(line);
        }

        var batched = new CommandLogBuffer(maxLines: 3);
        batched.AddRange(["a", "b", "c", "d"]);

        Assert.Equal(oneByOne.Render(), batched.Render());
    }

    [Fact]
    public void Clearing_EmptiesIt_SoTheActivityCommandsGoBackToDisabled()
    {
        var sut = new CommandLogBuffer();
        sut.AddRange(["a", "b"]);

        sut.Clear();

        Assert.Equal(0, sut.Count);
        Assert.Equal(string.Empty, sut.Render());
    }
}
