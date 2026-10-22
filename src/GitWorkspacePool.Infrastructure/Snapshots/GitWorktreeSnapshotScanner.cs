using System.Diagnostics;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Discovers staged, unstaged, untracked and ignored changes without mutating the worktree.
/// </summary>
public sealed class GitWorktreeSnapshotScanner
{
    /// <summary>
    /// Inspects a worktree using machine-readable Git status and its linked index path.
    /// </summary>
    /// <param name="workspacePath">The trusted registered worktree path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot plan and exact original Git state.</returns>
    public async Task<WorktreeSnapshotScan> InspectAsync(
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        string status = await RunAsync(
            workspacePath,
            [
                "-c",
                "core.quotePath=false",
                "status",
                "--porcelain=v1",
                "-z",
                "--no-renames",
                "--untracked-files=all",
                "--ignored=matching",
            ],
            cancellationToken
        );
        // Inspect *all* index stages before creating snapshot storage. Porcelain
        // alone does not expose unsupported gitlinks or sparse-directory entries.
        string indexEntries = await RunAsync(
            workspacePath,
            ["ls-files", "--stage", "--sparse", "-z"],
            cancellationToken
        );
        RejectUnsupportedIndexEntries(indexEntries);

        string indexPath = (await RunAsync(
            workspacePath,
            ["rev-parse", "--git-path", "index"],
            cancellationToken
        )).Trim();

        if (!Path.IsPathFullyQualified(indexPath))
        {
            indexPath = Path.Combine(workspacePath, indexPath);
        }

        string head = (await RunAsync(
            workspacePath,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken
        )).Trim();

        List<string> changedPaths = [];

        foreach (string record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // --no-renames ensures one path per NUL-delimited status record.
            if (record.Length < 4 || record[2] != ' ')
            {
                throw new InvalidOperationException("Unexpected Git status framing.");
            }

            string relativePath = record[3..];

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException("Git returned an empty snapshot path.");
            }

            changedPaths.Add(relativePath);
        }

        return new WorktreeSnapshotScan(
            status,
            head,
            Path.GetFullPath(indexPath),
            changedPaths
        );
    }

    private static void RejectUnsupportedIndexEntries(string entries)
    {
        foreach (string entry in entries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = entry.IndexOf('\t');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Unexpected Git index entry framing.");
            }

            string[] fields = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[1].Length is not (40 or 64))
            {
                throw new InvalidOperationException("Unexpected Git index entry metadata.");
            }

            if (fields[2] != "0")
            {
                throw new InvalidOperationException(
                    "Unmerged Git index entries must be resolved before snapshot capture."
                );
            }

            if (fields[0] == "160000")
            {
                throw new InvalidOperationException(
                    "Git submodule entries are not supported by portable snapshots."
                );
            }

            if (fields[0] is not ("100644" or "100755" or "120000"))
            {
                throw new InvalidOperationException(
                    "Unsupported Git index mode (including sparse directories); snapshot refused."
                );
            }
        }
    }

    private static async Task<string> RunAsync(
        string workspacePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        ProcessStartInfo startInfo = new("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8,
        };
        GitProcessSafety.Configure(startInfo);
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(workspacePath);

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Git status.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string stdout = await output;
            string stderr = await error;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git workspace inspection failed ({process.ExitCode}): {stderr.Trim()}"
                );
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }
}
