using AgentReview.Core.Diff;
using AgentReview.Core.Git;

namespace AgentReview.Core.Tests;

public class DiffDocumentTests
{
    [Fact]
    public void Parses_hunk_headers_including_omitted_counts_and_binary()
    {
        var parsed = GitRepository.ParseHunks("diff --git a/x b/x\n@@ -3 +3,2 @@\n-a\n+b\n+c\n@@ -10,0 +12 @@ foo\n+d\n");
        Assert.False(parsed.IsBinary);
        Assert.Equal([new DiffHunk(3, 1, 3, 2), new DiffHunk(10, 0, 12, 1)], parsed.Hunks);
        Assert.True(GitRepository.ParseHunks("Binary files a/x and b/x differ\n").IsBinary);
    }

    [Fact]
    public void Builds_full_merged_view_with_changes_in_place()
    {
        var oldText = "a\nb\nc\nd\n";
        var newText = "a\nB\nc\nd\ne\n";
        // b -> B (line 2), e inserted after old line 4
        var doc = new DiffDocument(oldText, newText, [new DiffHunk(2, 1, 2, 1), new DiffHunk(4, 0, 5, 1)]);

        Assert.Equal(
        [
            new DiffRow(RowKind.Context, 1, 1, "a"),
            new DiffRow(RowKind.Removed, 2, null, "b"),
            new DiffRow(RowKind.Added, null, 2, "B"),
            new DiffRow(RowKind.Context, 3, 3, "c"),
            new DiffRow(RowKind.Context, 4, 4, "d"),
            new DiffRow(RowKind.Added, null, 5, "e"),
        ], doc.Rows);
        Assert.Equal([(1, 2), (5, 5)], doc.ChangeGroups);
    }

    [Fact]
    public void Handles_deletion_at_start_and_crlf()
    {
        var doc = new DiffDocument("x\r\ny\r\n", "y\r\n", [new DiffHunk(1, 1, 0, 0)]);
        Assert.Equal([new DiffRow(RowKind.Removed, 1, null, "x"), new DiffRow(RowKind.Context, 2, 1, "y")], doc.Rows);
    }
}
