using AgentReview.Core.Diff;

namespace AgentReview.Core.Regions;

/// <summary>Expands SQL changes to the complete batch (between GO lines) or statement (between semicolons).</summary>
public sealed class SqlStrategy : IRegionStrategy
{
    public bool CanHandle(string path) => path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Region> GetRegions(DiffDocument doc, ReviewOptions options)
    {
        foreach (var group in doc.ChangeGroups)
        {
            var (added, removed) = RegionHelpers.LineSpans(doc, group);
            var found = false;
            if (added is { } a && FindStatement(doc.NewLines, a.First, a.Last, options.MaxUnitLines) is { } ns)
            {
                var (s, e) = doc.RowsForNewLines(ns.Start, ns.End);
                yield return new Region(Math.Min(s, group.Start), Math.Max(e, group.End), Describe(doc.NewLines, ns));
                found = true;
            }
            if (removed is { } r && FindStatement(doc.OldLines, r.First, r.Last, options.MaxUnitLines) is { } os)
            {
                var (s, e) = doc.RowsForOldLines(os.Start, os.End);
                yield return new Region(Math.Min(s, group.Start), Math.Max(e, group.End), Describe(doc.OldLines, os));
                found = true;
            }
            if (!found)
                yield return RegionHelpers.ContextAround(doc, group.Start, group.End, options.ContextLines);
        }
    }

    private static string Describe(IReadOnlyList<string> lines, (int Start, int End) range)
    {
        for (var i = range.Start; i <= range.End; i++)
        {
            var t = lines[i - 1].Trim();
            if (t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal))
                return $"complete SQL statement `{RegionHelpers.Shorten(t).Replace('`', '\'')}`";
        }
        return "complete SQL statement";
    }

    private static (int Start, int End)? FindStatement(IReadOnlyList<string> lines, int first, int last, int maxLines)
    {
        var useBatches = lines.Any(IsGo);
        bool IsEnd(string l) => useBatches ? IsGo(l) : l.TrimEnd().EndsWith(';');

        var start = first;
        while (start > 1 && !IsEnd(lines[start - 2]))
            start--;
        while (start < first && lines[start - 1].Trim().Length == 0)
            start++;

        var end = last;
        while (end < lines.Count && !IsEnd(lines[end - 1]))
            end++;

        return end - start + 1 <= maxLines ? (start, end) : null;
    }

    private static bool IsGo(string line) => line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);
}
