namespace AgentReview.Core.Diff;

/// <summary>
/// The complete old and new versions of one file merged into a single sequence of rows,
/// so that any range of either version can be shown in full with its changes marked.
/// </summary>
public sealed class DiffDocument
{
    public string OldText { get; }
    public string NewText { get; }
    public IReadOnlyList<string> OldLines { get; }
    public IReadOnlyList<string> NewLines { get; }
    public IReadOnlyList<DiffRow> Rows { get; }

    /// <summary>Contiguous runs of changed rows, as inclusive row index ranges.</summary>
    public IReadOnlyList<(int Start, int End)> ChangeGroups { get; }

    private readonly int[] _rowOfOldLine;
    private readonly int[] _rowOfNewLine;

    public DiffDocument(string oldText, string newText, IReadOnlyList<DiffHunk> hunks)
    {
        OldText = oldText;
        NewText = newText;
        OldLines = SplitLines(oldText);
        NewLines = SplitLines(newText);
        Rows = BuildRows(OldLines, NewLines, hunks);

        _rowOfOldLine = new int[OldLines.Count + 1];
        _rowOfNewLine = new int[NewLines.Count + 1];
        var groups = new List<(int, int)>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.OldLine is { } o) _rowOfOldLine[o] = i;
            if (row.NewLine is { } n) _rowOfNewLine[n] = i;
            if (row.Kind == RowKind.Context)
                continue;
            if (groups.Count > 0 && groups[^1].Item2 == i - 1)
                groups[^1] = (groups[^1].Item1, i);
            else
                groups.Add((i, i));
        }
        ChangeGroups = groups;
    }

    public int Additions => Rows.Count(r => r.Kind == RowKind.Added);
    public int Deletions => Rows.Count(r => r.Kind == RowKind.Removed);

    /// <summary>Row range covering lines <paramref name="first"/>..<paramref name="last"/> (1-based) of the new file.</summary>
    public (int Start, int End) RowsForNewLines(int first, int last) =>
        (_rowOfNewLine[Math.Clamp(first, 1, NewLines.Count)], _rowOfNewLine[Math.Clamp(last, 1, NewLines.Count)]);

    /// <summary>Row range covering lines <paramref name="first"/>..<paramref name="last"/> (1-based) of the old file.</summary>
    public (int Start, int End) RowsForOldLines(int first, int last) =>
        (_rowOfOldLine[Math.Clamp(first, 1, OldLines.Count)], _rowOfOldLine[Math.Clamp(last, 1, OldLines.Count)]);

    /// <summary>Splits text the same way git counts lines: on '\n', with no extra line after a trailing newline.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var result = new string[count];
        for (var i = 0; i < count; i++)
            result[i] = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
        return result;
    }

    private static List<DiffRow> BuildRows(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<DiffHunk> hunks)
    {
        var rows = new List<DiffRow>(Math.Max(oldLines.Count, newLines.Count) + 16);
        int o = 1, n = 1;

        void AddContextUntil(int oldLineExclusive)
        {
            while (o < oldLineExclusive && o <= oldLines.Count)
            {
                rows.Add(new DiffRow(RowKind.Context, o, n, n <= newLines.Count ? newLines[n - 1] : oldLines[o - 1]));
                o++;
                n++;
            }
        }

        foreach (var hunk in hunks.OrderBy(h => h.OldStart))
        {
            AddContextUntil(hunk.OldCount == 0 ? hunk.OldStart + 1 : hunk.OldStart);
            for (var i = 0; i < hunk.OldCount && o <= oldLines.Count; i++, o++)
                rows.Add(new DiffRow(RowKind.Removed, o, null, oldLines[o - 1]));
            for (var i = 0; i < hunk.NewCount && n <= newLines.Count; i++, n++)
                rows.Add(new DiffRow(RowKind.Added, null, n, newLines[n - 1]));
        }
        AddContextUntil(int.MaxValue);
        // Defensive: if the hunks and contents ever disagree, never silently drop new lines.
        for (; n <= newLines.Count; n++)
            rows.Add(new DiffRow(RowKind.Added, null, n, newLines[n - 1]));
        return rows;
    }
}
