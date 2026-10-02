using AgentReview.Core.Diff;

namespace AgentReview.Core.Regions;

/// <summary>
/// Language-agnostic fallback (JSON, YAML, XML, HTML, Razor, CSS, TS/JS, ...): expands a change to the
/// enclosing indented block, from its header line down to its closing line.
/// </summary>
public sealed class IndentBlockStrategy : IRegionStrategy
{
    public bool CanHandle(string path) => true;

    public IEnumerable<Region> GetRegions(DiffDocument doc, ReviewOptions options)
    {
        foreach (var group in doc.ChangeGroups)
        {
            var (added, removed) = RegionHelpers.LineSpans(doc, group);
            var found = false;
            if (added is { } a && FindBlock(doc.NewLines, a.First, a.Last, options.MaxUnitLines) is { } nb)
            {
                var (s, e) = doc.RowsForNewLines(nb.Start, nb.End);
                yield return new Region(Math.Min(s, group.Start), Math.Max(e, group.End), Describe(doc.NewLines, nb.Start, nb.End));
                found = true;
            }
            if (removed is { } r && FindBlock(doc.OldLines, r.First, r.Last, options.MaxUnitLines) is { } ob)
            {
                var (s, e) = doc.RowsForOldLines(ob.Start, ob.End);
                yield return new Region(Math.Min(s, group.Start), Math.Max(e, group.End), Describe(doc.OldLines, ob.Start, ob.End));
                found = true;
            }
            if (!found)
                yield return RegionHelpers.ContextAround(doc, group.Start, group.End, options.ContextLines);
        }
    }

    private static string Describe(IReadOnlyList<string> lines, int start, int end) =>
        $"complete enclosing block `{RegionHelpers.Shorten(lines[start - 1]).Replace('`', '\'')}`";

    /// <summary>Finds the block enclosing lines first..last (1-based); null if the change is at top level or the block is too big.</summary>
    internal static (int Start, int End)? FindBlock(IReadOnlyList<string> lines, int first, int last, int maxLines)
    {
        int? minIndent = null;
        for (var i = first; i <= last; i++)
            if (Indent(lines[i - 1]) is { } ind)
                minIndent = Math.Min(minIndent ?? int.MaxValue, ind);
        if (minIndent is not > 0)
            return null;

        var header = first - 1;
        while (header >= 1 && !(Indent(lines[header - 1]) < minIndent))
            header--;
        if (header < 1)
            return null;
        var headerIndent = Indent(lines[header - 1]);

        // A lone opening bracket line (Allman style): include the line that names the block.
        var trimmedHeader = lines[header - 1].Trim();
        if (trimmedHeader is "{" or "[" or "(" && header > 1)
            header--;

        var footer = last + 1;
        while (footer <= lines.Count && !(Indent(lines[footer - 1]) < minIndent))
            footer++;
        var end = footer <= lines.Count && Indent(lines[footer - 1]) == headerIndent && IsCloser(lines[footer - 1])
            ? footer
            : footer - 1;
        while (end > last && Indent(lines[end - 1]) is null)
            end--; // trailing blank lines

        return end - header + 1 <= maxLines ? (header, end) : null;
    }

    private static bool IsCloser(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith('}') || t.StartsWith(']') || t.StartsWith(')') || t.StartsWith("</", StringComparison.Ordinal)
               || t.StartsWith("end", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Indentation width (tab = 4), or null for blank lines.</summary>
    private static int? Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else return width;
        }
        return null;
    }
}
