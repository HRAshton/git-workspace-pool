using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Core.Application.WarmPool;

public static class LegacySlotExtensions
{
    public static Task<OpenWorkspaceResult> AcquireAsync(this AcquireExplicitReadySlotService service,
        PullRequestReference request, RepositoryRegistration repository, Guid slotId, long generation, CancellationToken token)
    {
        if (!request.RepositoryKey.Equals(repository.RepositoryKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The request does not belong to the trusted repository.");
        return service.AcquireCoreAsync(request.Provider + ":" + request.RepositoryKey, request.Number,
            repository.LocalPath, repository.WorkspaceRoot, slotId, generation,
            async (_, cancellation) =>
            {
                await service.Git.FetchPullRequestAsync(repository.LocalPath, request.Number, cancellation);
                return await service.Git.GetPullRequestCommitAsync(repository.LocalPath, request.Number, cancellation);
            }, token);
    }

    public static Task<WorkspaceSession> ReconcileAsync(this ReconcileExplicitReadySlotService service,
        Guid slotId, RepositoryRegistration repository, CancellationToken token) => service.ReconcileCoreAsync(
            slotId, null, "github:" + repository.RepositoryKey, repository.LocalPath, repository.WorkspaceRoot, token);

    public static Task<WorkspaceSession> ReconcileSelectedAsync(this ReconcileExplicitReadySlotService service,
        WorkspaceReadySlotReservation selected, RepositoryRegistration repository, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return service.ReconcileCoreAsync(selected.Lease.SlotId, selected, "github:" + repository.RepositoryKey,
            repository.LocalPath, repository.WorkspaceRoot, token);
    }
}
