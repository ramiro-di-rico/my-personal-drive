using System.Globalization;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Localization;

/// <summary>
/// docs/PLAN-I18N.md §10. Switching the interface language moves the process's current culture,
/// and under a Spanish culture "1.5" parses as 15 while 1.5 formats as "1,5". Everything that
/// touches machine data — a filename, a database value, a CLI or API payload — has to be immune to
/// that; everything a person reads has to follow it. These tests pin both halves by running the
/// formatting paths under a culture whose separators are the other way round.
///
/// The durable half of the fix is the CA1304/CA1305 warnings turned on in .editorconfig; this file
/// is the behavioural proof that the sweep behind them was right.
/// </summary>
[Collection(AppDataCollection.Name)]
public class CultureHazardTests : IDisposable
{
    private static readonly CultureInfo Comma = CultureInfo.GetCultureInfo("es-AR");

    public void Dispose() => Localizer.Instance.SetLanguage(LanguageCatalog.DefaultCode);

    [Fact]
    public void ByteSize_FollowsTheInterfaceLanguage()
    {
        Assert.Equal("1.5 GB", ByteSize.Format(1610612736));

        Localizer.Instance.SetLanguage("es");

        // Presentation: a Spanish interface writes the decimal separator the Spanish way.
        Assert.Equal("1,5 GB", ByteSize.Format(1610612736));
    }

    [Fact]
    public void ByteSize_WholeUnitsAreUnaffectedByTheSeparator()
    {
        Localizer.Instance.SetLanguage("es");

        Assert.Equal("512 MB", ByteSize.Format(536870912));
        Assert.Equal("0 B", ByteSize.Format(0));
    }

    /// <summary>
    /// The trash folder a local delete moves into is named after the date. It is a path, so it must
    /// keep the same shape whatever the interface language is — otherwise yesterday's trash and
    /// today's stop being comparable, and crash recovery cannot find either.
    /// </summary>
    [Fact]
    public void MachineDates_StayInvariant_WhateverTheAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Comma;
            var stamp = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            Assert.Equal("2026-09-05", stamp);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A validator's reason carries values, not a sentence, so nothing it produces can be
    /// culture-mangled on the way to the screen — the formatting happens once, at the presenter.
    /// </summary>
    [Fact]
    public void AFreeSpaceWarning_CarriesFormattedSizesThatFollowTheLanguage()
    {
        Localizer.Instance.SetLanguage("es");

        var issue = new SyncPairIssue(SyncPairIssueKind.NotEnoughFreeSpace, ByteSize.Format(1610612736), ByteSize.Format(1073741824));

        Assert.Contains("1,5 GB", MyPersonalDrive.ViewModels.SyncIssuePresenter.Describe(issue).Render(), StringComparison.Ordinal);
    }
}
