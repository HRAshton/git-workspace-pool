using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.CrashProbe;

/// <summary>
/// Disposable E2E subprocess; its parent test kills it at a named durability boundary.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || !Enum.TryParse(args[5], out SnapshotCheckpoint checkpoint))
        {
            Console.Error.WriteLine(
                "Usage: export|restore <db> <owner-or-snapshot-id> <storage-root> <signal-file> <checkpoint>"
            );
            return 2;
        }

        try
        {
            ISnapshotCheckpointSink sink = new BlockingCheckpointSink(checkpoint, args[4]);
            SqliteWorkspaceSessionJournal journal = new(args[1]);
            GitCliWorkspaceGit git = new();

            switch (args[0])
            {
                case "export":
                {
                    CaptureWorkspaceSnapshotService service = new(
                        journal,
                        new FileWorkspaceOperationLock(
                            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "locks")
                        ),
                        git,
                        new VerifiedWorkspaceSnapshotExporter(
                            args[3],
                            new GitWorktreeSnapshotScanner(),
                            sink
                        )
                    );
                    _ = await service.CaptureAsync(Guid.Parse(args[2]), CancellationToken.None);
                    break;
                }

                case "restore":
                {
                    RestoreWorkspaceSnapshotService service = new(
                        journal,
                        new VerifiedWorkspaceSnapshotRestorer(
                            args[3],
                            git,
                            new GitWorktreeSnapshotScanner(),
                            sink
                        )
                    );
                    _ = await service.RestoreAsync(Guid.Parse(args[2]), CancellationToken.None);
                    break;
                }

                default:
                    Console.Error.WriteLine("Unsupported crash-probe operation.");
                    return 2;
            }

            Console.Error.WriteLine("The requested crash checkpoint was never reached.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 4;
        }

        return 0;
    }

    private sealed class BlockingCheckpointSink(
        SnapshotCheckpoint target,
        string signalPath
    ) : ISnapshotCheckpointSink
    {
        public async ValueTask OnCheckpointAsync(
            SnapshotCheckpoint checkpoint,
            string operationPath,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint != target)
            {
                return;
            }

            string signal = checkpoint + "\n" + operationPath;
            await File.WriteAllTextAsync(signalPath, signal, cancellationToken);

            // The parent process must terminate this subprocess. A managed throw
            // would not test operating-system process death or lock release.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
