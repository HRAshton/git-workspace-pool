using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Snapshots;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Termination tests using a separate dotnet process and an actual OS process kill.
/// </summary>
public sealed class SubprocessCrashTests
{
    /// <summary>
    /// Executes a disposable subprocess, waits until it reaches a durable
    /// checkpoint, then kills it. Never kills an unrelated process.
    /// </summary>
    public static Task<string> KillAtCheckpointAsync(
        string operation,
        string database,
        Guid id,
        string storageRoot,
        SnapshotCheckpoint checkpoint,
        string signalRoot
    ) => KillAtNamedCheckpointAsync(
        operation, database, id, storageRoot, checkpoint.ToString(), signalRoot
    );

    /// <summary>Terminates an isolated parking subprocess after a durable Git or SQLite step.</summary>
    public static Task<string> KillAtParkingCheckpointAsync(
        string operation,
        string database,
        Guid id,
        string storageRoot,
        WorkspaceParkingCheckpoint checkpoint,
        string signalRoot
    ) => KillAtNamedCheckpointAsync(
        operation, database, id, storageRoot, checkpoint.ToString(), signalRoot
    );

    private static async Task<string> KillAtNamedCheckpointAsync(
        string operation,
        string database,
        Guid id,
        string storageRoot,
        string checkpoint,
        string signalRoot
    )
    {
        string signal = Path.Combine(signalRoot, Guid.NewGuid().ToString("N") + ".signal");
        string configuration = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
        ).Parent!.Name;
        string probeDll = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "GitWorkspacePool.CrashProbe",
                "bin",
                configuration,
                "net10.0",
                "GitWorkspacePool.CrashProbe.dll"
            )
        );

        Assert.True(File.Exists(probeDll), "Crash-probe project was not built: " + probeDll);

        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(probeDll);
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(id.ToString("D"));
        start.ArgumentList.Add(storageRoot);
        start.ArgumentList.Add(signal);
        start.ArgumentList.Add(checkpoint.ToString());
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start disposable crash-probe process.");

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

            // File.Exists can become true while the child is still writing.
            // Open with FileShare.None and retry sharing violations so the
            // checkpoint is only observed after the complete signal is flushed.
            string? content = null;

            while (content is null)
            {
                if (child.HasExited)
                {
                    string error = await child.StandardError.ReadToEndAsync(deadline.Token);
                    throw new InvalidOperationException(
                        $"Crash probe exited before {checkpoint} with {child.ExitCode}: {error}"
                    );
                }

                if (File.Exists(signal))
                {
                    try
                    {
                        await using FileStream stream = new(
                            signal,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None
                        );
                        using StreamReader reader = new(stream);
                        string candidate = await reader.ReadToEndAsync(deadline.Token);
                        int separator = candidate.IndexOf('\n');

                        if (separator >= 0 && separator + 1 < candidate.Length)
                        {
                            content = candidate;
                        }
                    }
                    catch (IOException)
                    {
                        // The child still owns the signal file; retry until it closes.
                    }
                }

                if (content is null)
                {
                    await Task.Delay(35, deadline.Token);
                }
            }

            string[] lines = content.Split('\n', 2);
            Assert.Equal(checkpoint, lines[0]);
            Assert.Equal(2, lines.Length);

            Assert.False(child.HasExited);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.HasExited);
            return lines[1];
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
