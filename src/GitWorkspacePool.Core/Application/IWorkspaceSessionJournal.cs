using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Atomically reserves workspace ownership and persists the preparation journal.
/// </summary>
public interface IWorkspaceSessionJournal
{
    /// <summary>
    /// Returns the existing owner, or durably reserves an unused PR/path pair.
    /// </summary>
    /// <param name="repositoryKey">Provider-qualified repository identity.</param>
    /// <param name="pullRequestNumber">The PR number.</param>
    /// <param name="sourcePath">Trusted source clone.</param>
    /// <param name="workspacePath">Managed workspace path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable session record.</returns>
    Task<WorkspaceSession> GetOrCreateAsync(
        string repositoryKey,
        int pullRequestNumber,
        string sourcePath,
        string workspacePath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Records an immutable target commit before checkout can modify the filesystem.
    /// </summary>
    /// <param name="operationId">The owning operation.</param>
    /// <param name="commitSha">Full Git object ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PinCommitAsync(Guid operationId, string commitSha, CancellationToken cancellationToken);

    /// <summary>
    /// Completes a verified allocation or reconciles a previously interrupted allocation.
    /// </summary>
    /// <param name="operationId">The owning operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ActivateAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Suspends an active session without releasing or moving its worktree.
    /// </summary>
    /// <param name="operationId">Existing active session owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SuspendAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Reactivates a suspended session after its original worktree is verified.
    /// </summary>
    /// <param name="operationId">Existing suspended session owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Quarantines an inconsistent record without deleting or moving user files.
    /// </summary>
    /// <param name="operationId">The owning operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken);
}
