namespace GitWorkspacePool.Core.Domain;

/// <summary>Fixed local timing categories. User URLs, paths, branch names and errors are not telemetry fields.</summary>
public enum WorkspaceOperation
{
    OpenBranch = 0,
    OpenPullRequest = 1,
    Snapshot = 2,
    Restore = 3,
    Suspend = 4,
    Park = 5,
    Unpark = 6,
    ReleaseSlot = 7,
    AcquireSlot = 8,
    RecoverSlot = 9,
    TransferArtifacts = 10,
    InspectStorage = 11,
    RefreshBaseline = 12,
    InspectPool = 13,
    RegisterRepository = 14,
}

/// <summary>The observed outcome of a user operation.</summary>
public enum WorkspaceOperationOutcome
{
    Completed = 0,
    Cancelled = 1,
    Failed = 2,
}

/// <summary>A privacy-limited local measurement; elapsed time ends when the application operation returns.</summary>
public sealed record WorkspaceTimingSample(
    DateTimeOffset Timestamp,
    WorkspaceOperation Operation,
    double ElapsedMilliseconds,
    WorkspaceOperationOutcome Outcome
);
