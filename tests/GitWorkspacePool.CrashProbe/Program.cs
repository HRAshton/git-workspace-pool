using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.CrashProbe;

/// <summary>
/// Disposable E2E subprocess; its parent deliberately kills it at a durability boundary.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine(
                "Usage: export|restore|park|unpark|prepare-release|release|acquire "
                    + "<db> <session-snapshot-preparation-or-slot-id> "
                    + "<storage-root-or-pr-url> <signal-file> <checkpoint>"
            );
            return 2;
        }

        try
        {
            SqliteWorkspaceSessionJournal journal = new(args[1]);
            GitCliWorkspaceGit git = new();

            if (args[0] is "prepare-release" or "release" or "acquire")
            {
                if (!Enum.TryParse(args[5], out WorkspaceSlotHandoffCheckpoint point))
                {
                    Console.Error.WriteLine("Unknown slot handoff checkpoint.");
                    return 2;
                }

                FileWorkspaceOperationLock locks = new(
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "locks")
                );
                SqliteWorkspaceSlotRegistry slots = new(args[1]);
                BlockingSlotCheckpointSink sink = new(point, args[4]);
                AssessWorkspaceReleaseService assessment = new(
                    journal, slots, locks, new GitWorkspaceParkingOperations(git)
                );

                switch (args[0])
                {
                    case "prepare-release":
                    {
                        PrepareWorkspaceSlotReleaseService service = new(
                            journal, locks, assessment, slots, sink
                        );
                        _ = await service.PrepareAsync(Guid.Parse(args[2]), CancellationToken.None);
                        break;
                    }

                    case "release":
                    {
                        FinalizeWorkspaceSlotReleaseService service = new(
                            journal, locks, assessment, slots, sink
                        );
                        _ = await service.FinalizeAsync(Guid.Parse(args[2]), CancellationToken.None);
                        break;
                    }

                    case "acquire":
                    {
                        Guid slotId = Guid.Parse(args[2]);
                        WorkspaceSlot slot = await slots.FindSlotAsync(
                            slotId, CancellationToken.None
                        ) ?? throw new InvalidOperationException("Missing Ready slot.");
                        if (!PullRequestReference.TryParse(
                            args[3], out PullRequestReference? request
                        ) || request is null)
                        {
                            throw new ArgumentException("An exact GitHub PR URL is required.");
                        }

                        string root = Path.GetDirectoryName(slot.WorkspacePath)
                            ?? throw new InvalidOperationException("Missing slot root.");
                        AcquireExplicitReadySlotService service = new(
                            slots, slots, new WorkspaceReadySlotSafety(),
                            git, new WorkspaceDirectories(), journal, locks, sink
                        );
                        _ = await service.AcquireAsync(
                            request,
                            new RepositoryRegistration(
                                request.RepositoryKey, slot.SourcePath, root
                            ),
                            slotId, slot.Generation, CancellationToken.None
                        );
                        break;
                    }
                }
            }
            else if (args[0] is "park" or "unpark")
            {
                if (!Enum.TryParse(args[5], out WorkspaceParkingCheckpoint parkingPoint))
                {
                    Console.Error.WriteLine("Unknown parking checkpoint.");
                    return 2;
                }

                Guid ownerId = Guid.Parse(args[2]);
                ParkWorkspaceSessionService service = new(
                    journal,
                    journal,
                    new FileWorkspaceOperationLock(
                        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "locks")
                    ),
                    new GitWorkspaceParkingOperations(git),
                    new BlockingParkingCheckpointSink(
                        parkingPoint,
                        args[4],
                        journal,
                        ownerId
                    )
                );

                if (args[0] == "park")
                {
                    _ = await service.ParkAsync(ownerId, CancellationToken.None);
                }
                else
                {
                    _ = await service.UnparkAsync(ownerId, CancellationToken.None);
                }
            }
            else
            {
                if (!Enum.TryParse(args[5], out SnapshotCheckpoint point))
                {
                    Console.Error.WriteLine("Unknown snapshot checkpoint.");
                    return 2;
                }

                ISnapshotCheckpointSink sink = new BlockingCheckpointSink(point, args[4]);

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
                                journal,
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
            }

            Console.Error.WriteLine("Requested crash checkpoint was never reached.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 4;
        }
    }

    private sealed class BlockingSlotCheckpointSink(
        WorkspaceSlotHandoffCheckpoint target,
        string signalPath
    ) : IWorkspaceSlotHandoffCheckpointSink
    {
        public async ValueTask AfterAsync(
            WorkspaceSlotHandoffCheckpoint checkpoint,
            string workspacePath,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint != target)
            {
                return;
            }

            await File.WriteAllTextAsync(
                signalPath, checkpoint + "\n" + workspacePath, cancellationToken
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
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

            await File.WriteAllTextAsync(
                signalPath,
                checkpoint + "\n" + operationPath,
                cancellationToken
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class BlockingParkingCheckpointSink(
        WorkspaceParkingCheckpoint target,
        string signalPath,
        SqliteWorkspaceSessionJournal journal,
        Guid sessionId
    ) : IWorkspaceParkingCheckpointSink
    {
        public async ValueTask AfterAsync(
            WorkspaceParkingCheckpoint checkpoint,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint != target)
            {
                return;
            }

            WorkspaceSession session = await journal.FindAsync(sessionId, cancellationToken)
                ?? throw new InvalidOperationException("Parking checkpoint has no journal owner.");

            string location = session.ParkingPath ?? session.WorkspacePath;
            await File.WriteAllTextAsync(
                signalPath,
                checkpoint + "\n" + location,
                cancellationToken
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
