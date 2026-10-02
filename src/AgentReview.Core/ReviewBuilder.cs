using System.Text.RegularExpressions;
using AgentReview.Core.Diff;
using AgentReview.Core.Git;
using AgentReview.Core.Regions;

namespace AgentReview.Core;

/// <summary>Builds a pull-request style review of <c>head</c> against <c>base</c> (changes since their merge-base).</summary>
public sealed class ReviewBuilder
{
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly IReadOnlyList<IRegionStrategy> _strategies = [new CSharpStrategy(), new SqlStrategy(), new IndentBlockStrategy()];

    public async Task<ReviewResult> BuildAsync(string repositoryPath, string baseRef, string headRef, ReviewOptions options, CancellationToken ct = default)
    {
        var repo = await GitRepository.OpenAsync(repositoryPath, ct);
        var baseSha = await repo.ResolveCommitAsync(baseRef, ct);
        var headSha = await repo.ResolveCommitAsync(headRef, ct);
        var mergeBase = await repo.GetMergeBaseAsync(baseSha, headSha, ct);
        var changed = await repo.GetChangedFilesAsync(mergeBase, headSha, ct);

        using var throttle = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, 8));
        var files = await Task.WhenAll(changed.Select(async file =>
        {
            await throttle.WaitAsync(ct);
            try { return await BuildFileAsync(repo, mergeBase, headSha, file, options, ct); }
            finally { throttle.Release(); }
        }));

        return new ReviewResult(repo.Root, baseRef, headRef, baseSha, headSha, mergeBase,
            files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), DateTimeOffset.Now);
    }

    private async Task<FileReview> BuildFileAsync(GitRepository repo, string fromSha, string toSha, ChangedFile file, ReviewOptions options, CancellationToken ct)
    {
        var hunks = await repo.GetFileHunksAsync(fromSha, toSha, file, ct);
        var added = hunks.Hunks.Sum(h => h.NewCount);
        var removed = hunks.Hunks.Sum(h => h.OldCount);

        FileReview Summary(string note) => new(file, added, removed, [], [], note);

        if (hunks.IsBinary)
            return Summary("Binary file; content not shown.");
        if (hunks.Hunks.Count == 0)
            return Summary(file.Status == ChangeStatus.Renamed ? "Renamed without content changes." : "No content changes (file mode or type change only).");
        if (IsOmitted(file.Path, options))
            return Summary("Lock, minified or generated file; content omitted.");

        var oldPath = file.OldPath ?? file.Path;
        var hasOld = file.Status != ChangeStatus.Added;
        var hasNew = file.Status != ChangeStatus.Deleted;
        if ((hasOld && await repo.GetFileSizeAsync(fromSha, oldPath, ct) > MaxFileBytes) ||
            (hasNew && await repo.GetFileSizeAsync(toSha, file.Path, ct) > MaxFileBytes))
            return Summary($"File larger than {MaxFileBytes / 1024 / 1024} MB; content not shown.");

        var oldText = hasOld ? await repo.GetFileTextAsync(fromSha, oldPath, ct) : "";
        var newText = hasNew ? await repo.GetFileTextAsync(toSha, file.Path, ct) : "";
        var doc = new DiffDocument(oldText, newText, hunks.Hunks);

        return new FileReview(file, doc.Additions, doc.Deletions, doc.Rows, BuildRegions(file, doc, options), null);
    }

    internal IReadOnlyList<ReviewRegion> BuildRegions(ChangedFile file, DiffDocument doc, ReviewOptions options)
    {
        if (doc.Rows.Count == 0)
            return [];

        // Entirely new or entirely deleted content: show it all.
        if (doc.Rows.All(r => r.Kind == RowKind.Added) || doc.Rows.All(r => r.Kind == RowKind.Removed))
        {
            var what = doc.Rows[0].Kind == RowKind.Added ? "new file, shown in full" : "deleted file, previous content shown in full";
            if (doc.Rows.Count <= options.MaxWholeFileLines)
                return [new ReviewRegion(0, doc.Rows.Count - 1, [what], [])];
            return [new ReviewRegion(0, options.MaxWholeFileLines - 1, [what],
                [$"Only the first {options.MaxWholeFileLines} of {doc.Rows.Count} lines are shown."])];
        }

        var isCSharp = _strategies[0].CanHandle(file.Path);
        if (!isCSharp && Math.Max(doc.OldLines.Count, doc.NewLines.Count) <= options.SmallFileLines)
            return [new ReviewRegion(0, doc.Rows.Count - 1, ["entire file (small file, shown in full)"], [])];

        var strategy = _strategies.First(s => s.CanHandle(file.Path));
        var regions = strategy.GetRegions(doc, options).ToList();

        // Guarantee: every changed row is inside some region.
        var covered = new bool[doc.Rows.Count];
        foreach (var r in regions)
            for (var i = Math.Max(0, r.StartRow); i <= Math.Min(r.EndRow, doc.Rows.Count - 1); i++)
                covered[i] = true;
        for (var i = 0; i < doc.Rows.Count; i++)
            if (doc.Rows[i].Kind != RowKind.Context && !covered[i])
                regions.Add(RegionHelpers.ContextAround(doc, i, i, options.ContextLines));

        return Merge(regions, doc.Rows.Count);
    }

    /// <summary>Sorts and merges overlapping or nearly adjacent regions (gaps of up to 3 lines are filled in).</summary>
    internal static IReadOnlyList<ReviewRegion> Merge(IEnumerable<Region> regions, int rowCount)
    {
        const int maxGapToFill = 3;
        var merged = new List<(int Start, int End, List<string> Descriptions, List<string> Notes)>();

        foreach (var r in regions.OrderBy(r => r.StartRow).ThenByDescending(r => r.EndRow))
        {
            var start = Math.Max(0, r.StartRow);
            var end = Math.Min(rowCount - 1, r.EndRow);
            if (merged.Count > 0 && start <= merged[^1].End + 1 + maxGapToFill)
            {
                var last = merged[^1];
                last.End = Math.Max(last.End, end);
                merged[^1] = last;
            }
            else
            {
                merged.Add((start, end, [], []));
            }
            AddDistinct(merged[^1].Descriptions, r.Description);
            if (r.Note is not null)
                AddDistinct(merged[^1].Notes, r.Note);
        }

        return merged.Select(m => new ReviewRegion(m.Start, m.End, Simplify(m.Descriptions), m.Notes)).ToList();
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!list.Contains(value))
            list.Add(value);
    }

    // "lines around the change" adds nothing when the region also contains a real code unit.
    private static List<string> Simplify(List<string> descriptions)
    {
        var meaningful = descriptions.Where(d => !d.StartsWith("lines around the change", StringComparison.Ordinal)).ToList();
        return meaningful.Count > 0 ? meaningful : descriptions;
    }

    private static bool IsOmitted(string path, ReviewOptions options)
    {
        var fileName = Path.GetFileName(path);
        return options.OmitContentPatterns.Any(p =>
            Regex.IsMatch(fileName, "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase));
    }
}
