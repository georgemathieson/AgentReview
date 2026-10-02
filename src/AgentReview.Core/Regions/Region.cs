using AgentReview.Core.Diff;

namespace AgentReview.Core.Regions;

/// <summary>A range of rows (inclusive) to show, with a human/AI readable explanation of what it is.</summary>
public sealed record Region(int StartRow, int EndRow, string Description, string? Note = null);

public interface IRegionStrategy
{
    bool CanHandle(string path);

    /// <summary>Returns regions that together cover the changed rows, expanded to meaningful code units.</summary>
    IEnumerable<Region> GetRegions(DiffDocument doc, ReviewOptions options);
}

internal static class RegionHelpers
{
    public static Region ContextAround(DiffDocument doc, int startRow, int endRow, int contextLines) =>
        new(Math.Max(0, startRow - contextLines), Math.Min(doc.Rows.Count - 1, endRow + contextLines),
            $"lines around the change (±{contextLines} lines of context)");

    /// <summary>Line ranges (1-based, inclusive) of added lines (new file) and removed lines (old file) in a change group.</summary>
    public static ((int First, int Last)? Added, (int First, int Last)? Removed) LineSpans(DiffDocument doc, (int Start, int End) group)
    {
        (int, int)? added = null, removed = null;
        for (var i = group.Start; i <= group.End; i++)
        {
            var row = doc.Rows[i];
            if (row.NewLine is { } n && row.Kind == RowKind.Added)
                added = added is { } a ? (a.Item1, n) : (n, n);
            if (row.OldLine is { } o && row.Kind == RowKind.Removed)
                removed = removed is { } r ? (r.Item1, o) : (o, o);
        }
        return (added, removed);
    }

    public static string Shorten(string text, int max = 80)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
