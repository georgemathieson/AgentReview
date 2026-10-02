using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentReview.Core.Git;

public sealed class GitException(string message) : Exception(message);

internal sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs the git executable with arguments passed verbatim (no shell, no quoting issues).</summary>
internal static class GitRunner
{
    // Forces stable, machine-readable output regardless of the user's own git config.
    private static readonly string[] CommonArgs =
        ["--no-pager", "-c", "core.quotepath=false", "-c", "color.ui=never", "-c", "diff.noprefix=false"];

    public static async Task<GitResult> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in CommonArgs.Concat(args))
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitException($"Could not start git. Make sure git is installed and on your PATH. ({ex.Message})");
        }

        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new GitResult(process.ExitCode, await stdOut, await stdErr);
    }
}
