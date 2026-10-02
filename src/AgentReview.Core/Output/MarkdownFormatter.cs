using System.Text;
using AgentReview.Core.Diff;

namespace AgentReview.Core.Output;

/// <summary>
/// Renders a review as Markdown designed for an AI reviewer: an explicit legend, complete code units,
/// a marker column that separates changes from context, and explicit notes wherever code is omitted.
/// </summary>
public static class MarkdownFormatter
{
    public static string Format(ReviewResult review)
    {
        var sb = new StringBuilder();
        var repoName = Path.GetFileName(review.RepositoryRoot.TrimEnd('/', '\\'));

        sb.AppendLine($"# Code changes on `{review.HeadRef}` compared with `{review.BaseRef}`");
        sb.AppendLine();
        sb.AppendLine($"- Repository: `{repoName}`");
        sb.AppendLine($"- Base: `{review.BaseRef}`, compared from merge-base commit `{Short(review.MergeBaseSha)}`");
        sb.AppendLine($"- Head: `{review.HeadRef}` at commit `{Short(review.HeadSha)}`");
        sb.AppendLine($"- {review.Files.Count} file(s) changed, {review.Additions} line(s) added, {review.Deletions} line(s) removed");
        sb.AppendLine();
        AppendLegend(sb, review);

        if (review.Files.Count == 0)
        {
            sb.AppendLine("There are no changes between these branches.");
            return sb.ToString();
        }

        sb.AppendLine("## Changed files");
        sb.AppendLine();
        sb.AppendLine("| # | File | Change | Added | Removed |");
        sb.AppendLine("|---|------|--------|------:|--------:|");
        for (var i = 0; i < review.Files.Count; i++)
        {
            var f = review.Files[i];
            sb.AppendLine($"| {i + 1} | `{f.Path}` | {StatusText(f.File)} | {f.Additions} | {f.Deletions} |");
        }
        sb.AppendLine();

        for (var i = 0; i < review.Files.Count; i++)
            AppendFile(sb, review.Files[i], i + 1);

        return sb.ToString();
    }

    private static void AppendLegend(StringBuilder sb, ReviewResult review)
    {
        sb.AppendLine("## How to read this diff");
        sb.AppendLine();
        sb.AppendLine($"This is a pull-request style diff. It contains only the changes made on `{review.HeadRef}` since it branched from `{review.BaseRef}`.");
        sb.AppendLine();
        sb.AppendLine("Each change is shown inside the **complete** code unit that contains it, not just a few lines around it. " +
                      "For C# this is the whole method, constructor, property, field, enum or type, including its doc comments and attributes. " +
                      "For other files it is the complete enclosing block or statement, or the entire file when the file is small.");
        sb.AppendLine();
        sb.AppendLine("Every code line begins with a marker, then the old line number, the new line number and `|`:");
        sb.AppendLine();
        sb.AppendLine("- `+` the line was **added** (it exists only in the new version; it has no old line number).");
        sb.AppendLine("- `-` the line was **removed** (it exists only in the old version; it has no new line number).");
        sb.AppendLine("- ` ` (a space) the line is **unchanged**. It exists in both versions and is shown only as context so the change can be understood. It is not part of the change.");
        sb.AppendLine();
        sb.AppendLine("Important:");
        sb.AppendLine();
        sb.AppendLine("- Only `+` and `-` lines are changes. For example, if only a doc comment line is marked, only the comment changed; the method body below it is shown unchanged for context.");
        sb.AppendLine("- Code units are shown in full. Nothing inside a shown region has been abridged, so a method that looks empty really is empty.");
        sb.AppendLine("- A line such as _\"⋯ 40 unchanged lines not shown ⋯\"_ between regions stands for code that is unchanged and unrelated to the change. That code still exists; it has not been deleted.");
        sb.AppendLine("- A modified line appears as a `-` line (old version) immediately followed by a `+` line (new version).");
        sb.AppendLine();
    }

    private static void AppendFile(StringBuilder sb, FileReview file, int number)
    {
        sb.AppendLine($"## {number}. `{file.Path}`");
        sb.AppendLine();
        sb.AppendLine($"Change: {StatusText(file.File)}, {file.Additions} line(s) added, {file.Deletions} line(s) removed.");
        if (file.File.OldPath is { } oldPath)
            sb.AppendLine($"Previously named `{oldPath}` ({file.File.Similarity}% similar).");
        if (file.Note is not null)
            sb.AppendLine($"Note: {file.Note}");
        sb.AppendLine();
        if (file.Regions.Count == 0)
            return;

        var lastRow = file.Rows.Count - 1;
        for (var r = 0; r < file.Regions.Count; r++)
        {
            var region = file.Regions[r];
            var gapStart = r == 0 ? 0 : file.Regions[r - 1].EndRow + 1;
            AppendGap(sb, file.Rows, gapStart, region.StartRow - 1);

            var rows = file.Rows.Skip(region.StartRow).Take(region.EndRow - region.StartRow + 1).ToList();
            var plus = rows.Count(x => x.Kind == RowKind.Added);
            var minus = rows.Count(x => x.Kind == RowKind.Removed);
            var nature = minus == 0 && plus == rows.Count ? "entirely new code"
                : plus == 0 && minus == rows.Count ? "entirely removed code"
                : $"{plus} line(s) added, {minus} line(s) removed";

            sb.AppendLine($"### Region {r + 1} of {file.Regions.Count}: {string.Join("; ", region.Descriptions)}");
            sb.AppendLine();
            sb.AppendLine($"{LineRangeText(rows)}. In this region: {nature}.");
            foreach (var note in region.Notes)
                sb.AppendLine($"Note: {note}");
            sb.AppendLine();
            AppendCode(sb, rows);
        }
        AppendGap(sb, file.Rows, file.Regions[^1].EndRow + 1, lastRow);
    }

    private static void AppendCode(StringBuilder sb, IReadOnlyList<DiffRow> rows)
    {
        var width = rows.Max(r => Math.Max(r.OldLine ?? 0, r.NewLine ?? 0)).ToString().Length;
        var fence = Fence(rows);
        sb.AppendLine(fence + "diff");
        foreach (var row in rows)
        {
            var marker = row.Kind switch { RowKind.Added => '+', RowKind.Removed => '-', _ => ' ' };
            var oldNo = (row.OldLine?.ToString() ?? "").PadLeft(width);
            var newNo = (row.NewLine?.ToString() ?? "").PadLeft(width);
            sb.Append(marker).Append(' ').Append(oldNo).Append(' ').Append(newNo).Append(" | ").AppendLine(row.Text);
        }
        sb.AppendLine(fence);
        sb.AppendLine();
    }

    private static void AppendGap(StringBuilder sb, IReadOnlyList<DiffRow> rows, int startRow, int endRow)
    {
        if (endRow < startRow)
            return;
        var count = endRow - startRow + 1;
        var lines = rows[startRow].NewLine is { } n1 && rows[endRow].NewLine is { } n2 ? $" (lines {n1}–{n2} of the new file)" : "";
        sb.AppendLine($"_⋯ {count} unchanged line(s) not shown{lines}. This code exists unchanged and is not part of the change. ⋯_");
        sb.AppendLine();
    }

    private static string LineRangeText(IReadOnlyList<DiffRow> rows)
    {
        var news = rows.Where(r => r.NewLine.HasValue).Select(r => r.NewLine!.Value).ToList();
        var olds = rows.Where(r => r.OldLine.HasValue).Select(r => r.OldLine!.Value).ToList();
        var parts = new List<string>();
        if (olds.Count > 0) parts.Add($"old lines {olds.Min()}–{olds.Max()}");
        if (news.Count > 0) parts.Add($"new lines {news.Min()}–{news.Max()}");
        return char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
    }

    /// <summary>A code fence longer than any run of backticks in the content, so content can never close it early.</summary>
    private static string Fence(IEnumerable<DiffRow> rows)
    {
        var longest = 0;
        foreach (var row in rows)
        {
            var run = 0;
            foreach (var c in row.Text)
            {
                run = c == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }
        return new string('`', Math.Max(3, longest + 1));
    }

    public static string StatusText(ChangedFile file) => file.Status switch
    {
        ChangeStatus.Added => "added",
        ChangeStatus.Deleted => "deleted",
        ChangeStatus.Renamed => "renamed",
        ChangeStatus.Copied => "copied",
        ChangeStatus.TypeChanged => "type changed",
        _ => "modified",
    };

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;
}
