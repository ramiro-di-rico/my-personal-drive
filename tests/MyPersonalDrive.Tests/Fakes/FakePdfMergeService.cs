using MyPersonalDrive.Services;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Stands in for <see cref="IPdfMergeService"/> so view-model tests can assert on the merge that
/// was <em>requested</em> — which files, in which order, to which name — without PdfPig, real PDF
/// bytes, or the CPU cost of a real merge. <see cref="PdfMergeServiceTests"/> is what proves the
/// real merge works.
/// </summary>
public sealed class FakePdfMergeService : IPdfMergeService
{
    /// <summary>Every merge asked for, in order.</summary>
    public List<(IReadOnlyList<string> Sources, string Output)> Merges { get; } = [];

    /// <summary>Page count handed back on success.</summary>
    public int PagesToReport { get; set; } = 7;

    /// <summary>When set, thrown instead of merging — the failure path.</summary>
    public Exception? ThrowOnMerge { get; set; }

    /// <summary>Whether the fake writes a placeholder file at the output path. The remote flow uploads that file, so it has to exist.</summary>
    public bool WriteOutputFile { get; set; } = true;

    public Task<int> MergeAsync(IReadOnlyList<string> localPaths, string outputPath, CancellationToken cancellationToken = default)
    {
        Merges.Add(([.. localPaths], outputPath));

        if (ThrowOnMerge is not null)
        {
            throw ThrowOnMerge;
        }

        if (WriteOutputFile)
        {
            File.WriteAllText(outputPath, "merged");
        }

        return Task.FromResult(PagesToReport);
    }
}
