namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>Read-only filesystem ancestry and trust checks for explicit slot acquisition.</summary>
public interface IWorkspaceReadySlotSafety
{
    /// <summary>Refuses reparse-point ancestors, nested source paths and unsupported aliases.</summary>
    void EnsureSafe(string sourceClone, string workspaceRoot, string slotPath);
}
