namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Optional controlled fault-injection boundary; omitted in production.
/// </summary>
public interface IWorkspaceParkingCheckpointSink
{
    /// <summary>Invoked after a durable side effect has completed.</summary>
    ValueTask AfterAsync(WorkspaceParkingCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>Durability boundaries for fault-injection tests.</summary>
public enum WorkspaceParkingCheckpoint
{
    ParkingIntentRecorded,
    ParkedWorktreeMoved,
    UnparkingIntentRecorded,
    UnparkedWorktreeMoved,
}
