using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Read-only inventory of durable slot and live lease integrity.
/// </summary>
public interface IWorkspaceSlotOwnershipAuditor
{
    /// <summary>
    /// Scans the complete bounded slot registry under a SQLite read snapshot.
    /// Throws rather than reporting falsely clean if the inventory is too large.
    /// </summary>
    Task<IReadOnlyList<WorkspaceSlotIntegrityFinding>> AuditAsync(
        CancellationToken cancellationToken
    );
}
