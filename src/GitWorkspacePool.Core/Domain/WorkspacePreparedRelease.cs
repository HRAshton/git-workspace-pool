namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// Immutable, read-only ownership detail for an incomplete slot release.
/// No result authorizes deletion, recycling or automatic transfer.
/// </summary>
/// <param name="PreparationId">Durable preparation record.</param>
/// <param name="SessionId">PR session holding the slot lease.</param>
/// <param name="SlotId">Protected slot.</param>
/// <param name="WorkspacePath">Original reserved slot path.</param>
/// <param name="Generation">Generation recorded when preparation was created.</param>
public sealed record WorkspacePreparedRelease(
    Guid PreparationId,
    Guid SessionId,
    Guid SlotId,
    string WorkspacePath,
    long Generation
);
