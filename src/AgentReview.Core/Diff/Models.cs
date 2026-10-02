namespace AgentReview.Core.Diff;

public enum ChangeStatus { Added, Deleted, Modified, Renamed, Copied, TypeChanged }

public sealed record ChangedFile(ChangeStatus Status, string Path, string? OldPath, int Similarity);

/// <summary>A hunk header from <c>git diff -U0</c>. A count of 0 means the start is the line *before* the change.</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);

public sealed record FileHunks(bool IsBinary, IReadOnlyList<DiffHunk> Hunks);

public enum RowKind { Context, Added, Removed }

/// <summary>One line of the merged view: the complete file with removed lines interleaved where they used to be.</summary>
public sealed record DiffRow(RowKind Kind, int? OldLine, int? NewLine, string Text);
