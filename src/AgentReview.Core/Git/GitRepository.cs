using System.Text.RegularExpressions;
using AgentReview.Core.Diff;

namespace AgentReview.Core.Git;

public sealed record BranchInfo(string Name, bool IsRemote, bool IsCurrent, DateTimeOffset LastCommit);

/// <summary>Read-only access to a local git repository via the git CLI.</summary>
public sealed partial class GitRepository
{
    public string Root { get; }

    private GitRepository(string root) => Root = root;

    public static async Task<GitRepository> OpenAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new GitException("Enter the path to a local git repository.");

        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        if (!Directory.Exists(fullPath))
            throw new GitException($"Directory not found: {fullPath}");

        var result = await GitRunner.RunAsync(fullPath, ["rev-parse", "--show-toplevel"], ct);
        if (result.ExitCode != 0)
            throw new GitException($"'{fullPath}' is not inside a git repository.");

        return new GitRepository(Path.GetFullPath(result.StdOut.Trim()));
    }

    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
    {
        var output = await RunAsync(ct, "for-each-ref",
            "--format=%(HEAD)%00%(refname)%00%(refname:short)%00%(symref)%00%(committerdate:iso-strict)", "refs/heads", "refs/remotes");

        var branches = new List<BranchInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length < 5 || parts[3].Length > 0)
                continue; // symbolic refs such as origin/HEAD
            DateTimeOffset.TryParse(parts[4], out var lastCommit);
            branches.Add(new BranchInfo(parts[2], parts[1].StartsWith("refs/remotes/", StringComparison.Ordinal), parts[0] == "*", lastCommit));
        }

        return branches
            .OrderBy(b => b.IsRemote)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves a branch name, tag or SHA to a full commit SHA.</summary>
    public async Task<string> ResolveCommitAsync(string revision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.StartsWith('-'))
            throw new GitException($"'{revision}' is not a valid branch name.");

        var result = await GitRunner.RunAsync(Root, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct);
        if (result.ExitCode != 0)
            throw new GitException($"'{revision}' is not a branch or commit in this repository.");
        return result.StdOut.Trim();
    }

    public async Task<string> GetMergeBaseAsync(string a, string b, CancellationToken ct = default)
    {
        var result = await GitRunner.RunAsync(Root, ["merge-base", a, b], ct);
        if (result.ExitCode != 0)
            throw new GitException("The two branches have no common ancestor, so a pull-request style diff is not possible.");
        return result.StdOut.Trim();
    }

    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string fromSha, string toSha, CancellationToken ct = default)
    {
        var output = await RunAsync(ct, "diff", "--name-status", "-z", "-M", "--no-ext-diff", fromSha, toSha);
        var tokens = output.Split('\0');
        var files = new List<ChangedFile>();

        for (var i = 0; i + 1 < tokens.Length && tokens[i].Length > 0;)
        {
            var status = tokens[i];
            var similarity = status.Length > 1 && int.TryParse(status.AsSpan(1), out var s) ? s : 0;
            switch (status[0])
            {
                case 'R' or 'C' when i + 2 < tokens.Length:
                    files.Add(new ChangedFile(status[0] == 'R' ? ChangeStatus.Renamed : ChangeStatus.Copied, tokens[i + 2], tokens[i + 1], similarity));
                    i += 3;
                    break;
                default:
                    var kind = status[0] switch
                    {
                        'A' => ChangeStatus.Added,
                        'D' => ChangeStatus.Deleted,
                        'T' => ChangeStatus.TypeChanged,
                        _ => ChangeStatus.Modified,
                    };
                    files.Add(new ChangedFile(kind, tokens[i + 1], null, similarity));
                    i += 2;
                    break;
            }
        }

        return files;
    }

    /// <summary>Gets the hunk headers for one file (zero context lines; the full text is fetched separately).</summary>
    public async Task<FileHunks> GetFileHunksAsync(string fromSha, string toSha, ChangedFile file, CancellationToken ct = default)
    {
        List<string> args = ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-textconv", "--diff-algorithm=histogram", "-M", fromSha, toSha, "--"];
        if (file.OldPath is not null)
            args.Add(file.OldPath);
        args.Add(file.Path);

        var output = await RunAsync(ct, [.. args]);
        return ParseHunks(output);
    }

    public async Task<string> GetFileTextAsync(string sha, string path, CancellationToken ct = default) =>
        await RunAsync(ct, "cat-file", "blob", $"{sha}:{path}");

    public async Task<long> GetFileSizeAsync(string sha, string path, CancellationToken ct = default) =>
        long.Parse((await RunAsync(ct, "cat-file", "-s", $"{sha}:{path}")).Trim());

    internal static FileHunks ParseHunks(string unifiedDiff)
    {
        var hunks = new List<DiffHunk>();
        var isBinary = false;
        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(line);
                if (!m.Success)
                    continue;
                hunks.Add(new DiffHunk(
                    int.Parse(m.Groups[1].Value),
                    m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1,
                    int.Parse(m.Groups[3].Value),
                    m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1));
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                isBinary = true;
            }
        }
        return new FileHunks(isBinary, hunks);
    }

    private async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var result = await GitRunner.RunAsync(Root, args, ct);
        if (result.ExitCode != 0)
            throw new GitException($"git {args[0]} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();
}
