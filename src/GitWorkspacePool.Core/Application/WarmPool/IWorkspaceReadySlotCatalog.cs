using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>Read-only bounded candidate inventory for explicitly released slots.</summary>
public interface IWorkspaceReadySlotCatalog
{
    /// <summary>
    /// Lists only slots already published Ready by the explicit release workflow.
    /// A returned candidate is advisory; reservation and Git preflight revalidate it.
    /// </summary>
    Task<IReadOnlyList<WorkspaceSlot>> ListReadyAsync(CancellationToken cancellationToken);
}
