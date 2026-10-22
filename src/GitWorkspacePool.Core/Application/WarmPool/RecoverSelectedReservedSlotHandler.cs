using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// User-selected recovery of an interrupted slot checkout. Resolves only
/// configured GitHub repositories and verifies their actual origin before
/// requesting locked, generation-checked, non-destructive activation.
/// </summary>
public sealed class RecoverSelectedReservedSlotHandler
{
    private readonly IWorkspaceReadySlotAllocator _allocator;
    private readonly IRepositoryCatalog _repositories;
    private readonly IRepositoryOriginVerifier _originVerifier;
    private readonly ReconcileExplicitReadySlotService _recovery;

    /// <summary>Composes trusted repository and verified checkout dependencies.</summary>
    public RecoverSelectedReservedSlotHandler(
        IWorkspaceReadySlotAllocator allocator,
        IRepositoryCatalog repositories,
        IRepositoryOriginVerifier originVerifier,
        ReconcileExplicitReadySlotService recovery
    )
    {
        _allocator = allocator;
        _repositories = repositories;
        _originVerifier = originVerifier;
        _recovery = recovery;
    }

    /// <summary>
    /// Explicitly finalizes only the selected durable generation and owner.
    /// Never creates a checkout, moves files, runs Git repair or releases a lease.
    /// </summary>
    public async Task<WorkspaceSession> HandleAsync(
        Guid slotId,
        long selectedGeneration,
        Guid selectedOwnerId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceReadySlotReservation reservation =
            await _allocator.FindReservedAsync(slotId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The selected interrupted allocation is no longer reserved."
                );

        if (
            selectedGeneration != reservation.Lease.Generation
            || selectedOwnerId != reservation.Session.OperationId
            || reservation.Lease.SlotId != slotId
            || reservation.Lease.SessionId != selectedOwnerId
        )
        {
            throw new InvalidOperationException(
                "The selected owner or slot generation has changed. No files were modified."
            );
        }

        string key = reservation.Session.RepositoryKey;
        if (!key.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported reserved repository provider.");
        }

        // A persisted owner is not sufficient evidence of trust. Reparse the
        // repository identity using the same strict browser URL rules.
        string url = "https://github.com/"
            + key["github:".Length..]
            + "/pull/"
            + reservation.Session.PullRequestNumber;
        if (
            !PullRequestReference.TryParse(url, out PullRequestReference? request)
            || request is null
            || !key.Equals(
                request.Provider + ":" + request.RepositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException("Invalid reserved PR identity.");
        }

        RepositoryRegistration trusted = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                "Configure the reserved PR's local clone before recovering this slot."
            );
        await _originVerifier.VerifyAsync(request, trusted, cancellationToken);

        // Re-read the reserved lease under its OS lock and compare both
        // selection tokens before any Preparing -> Active journal transition.
        return await _recovery.ReconcileAsync(
            slotId, trusted, cancellationToken, selectedGeneration, selectedOwnerId
        );
    }
}
