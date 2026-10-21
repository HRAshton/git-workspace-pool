namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// Couples a published snapshot with its durable workspace owner.
/// </summary>
/// <param name="Session">Original workspace ownership record.</param>
/// <param name="Receipt">Published, journalled snapshot receipt.</param>
public sealed record WorkspaceSnapshotRecord(
    WorkspaceSession Session,
    WorkspaceSnapshotReceipt Receipt
);
