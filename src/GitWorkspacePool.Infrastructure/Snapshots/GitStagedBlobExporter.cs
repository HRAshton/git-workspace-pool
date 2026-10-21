using System.Diagnostics;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Saves staged Git blob payloads separately from working files so the index remains restorable.
/// </summary>
public sealed class GitStagedBlobExporter
{
    /// <summary>
    /// Copies the stage-zero version of a file directly from Git's immutable object database.
    /// </summary>
    /// <param name="workspace">The trusted Git worktree.</param>
    /// <param name="relativePath">The Git-relative staged path.</param>
    /// <param name="destination">An app-owned new payload file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of staged bytes written.</returns>
    public async Task<long> ExportAsync(
        string workspace,
        string relativePath,
        string destination,
        CancellationToken cancellationToken
    )
    {
        ProcessStartInfo start = new("git")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        GitProcessSafety.Configure(start);
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(workspace);
        start.ArgumentList.Add("show");
        start.ArgumentList.Add(":" + relativePath);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not extract staged Git data.");

        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await using (FileStream target = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.WriteThrough
            ))
            {
                await process.StandardOutput.BaseStream.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }

            await process.WaitForExitAsync(cancellationToken);
            string stderr = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Could not capture the staged Git blob: {stderr.Trim()}"
                );
            }

            return new FileInfo(destination).Length;
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

    /// <summary>
    /// Selects staged content, refusing index conflicts that cannot be restored safely.
    /// </summary>
    /// <param name="porcelain">NUL-delimited, --no-renames porcelain v1 output.</param>
    /// <returns>Paths with staged payloads (not staged deletions).</returns>
    public static IReadOnlyList<string> FindStagedFiles(string porcelain)
    {
        List<string> paths = [];

        foreach (string record in porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length < 4 || record[2] != ' ')
            {
                throw new InvalidOperationException("Unexpected Git index status.");
            }

            string xy = record[..2];

            if (
                xy.Contains('U')
                || xy is "AA" or "DD"
            )
            {
                throw new InvalidOperationException(
                    "Unmerged index entries require resolution before a portable snapshot."
                );
            }

            if (xy[0] is not (' ' or '?' or '!' or 'D'))
            {
                paths.Add(record[3..]);
            }
        }

        return paths;
    }
}
