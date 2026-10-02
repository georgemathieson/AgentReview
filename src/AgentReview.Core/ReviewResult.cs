using AgentReview.Core.Diff;
using AgentReview.Core.Regions;

namespace AgentReview.Core;

public sealed record ReviewResult(
    string RepositoryRoot,
    string BaseRef,
    string HeadRef,
    string BaseSha,
    string HeadSha,
    string MergeBaseSha,
    IReadOnlyList<FileReview> Files,
    DateTimeOffset GeneratedAt)
{
    public int Additions => Files.Sum(f => f.Additions);
    public int Deletions => Files.Sum(f => f.Deletions);
}

/// <summary>A changed file and the regions of it that should be shown.</summary>
public sealed record FileReview(
    ChangedFile File,
    int Additions,
    int Deletions,
    IReadOnlyList<DiffRow> Rows,
    IReadOnlyList<ReviewRegion> Regions,
    string? Note)
{
    public string Path => File.Path;
}

/// <summary>A contiguous, merged range of rows to show. Rows outside every region are unchanged and omitted.</summary>
public sealed record ReviewRegion(int StartRow, int EndRow, IReadOnlyList<string> Descriptions, IReadOnlyList<string> Notes);
