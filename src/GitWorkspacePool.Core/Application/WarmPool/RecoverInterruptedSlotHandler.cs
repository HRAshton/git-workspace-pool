using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// User-confirmed entry point for finishing an interrupted Ready-slot checkout.
/// Verifies the exact menu selection and trusted origin before locked Git checks.
/// </summary>
public sealed class RecoverInterruptedSlotHandler
{
    private readonly IWorkspaceSlotReservationInventory _inventory;
    private readonly IRepositoryCatalog _repositories;
    private readonly IRepositoryOriginVerifier _originVerifier;
    private readonly ReconcileExplicitReadySlotService _reconcile;
    private readonly IEditorLauncher _editor;

    /// <summary>Composes read-only discovery, trusted origin, recovery and editor launch.</summary>
    public RecoverInterruptedSlotHandler(
        IWorkspaceSlotReservationInventory inventory,
        IRepositoryCatalog repositories,
        IRepositoryOriginVerifier originVerifier,
        ReconcileExplicitReadySlotService reconcile,
        IEditorLauncher editor
    )
    {
        _inventory = inventory;
        _repositories = repositories;
        _originVerifier = originVerifier;
        _reconcile = reconcile;
        _editor = editor;
    }

    /// <summary>
    /// Recovers only the exact explicitly selected reserved lease. Missing,
    /// dirty, mismatched or changed selections remain untouched; never checks
    /// out, cleans, moves or deletes a worktree.
    /// </summary>
    public async Task<WorkspaceSession> HandleAsync(
        Guid slotId,
        Guid expectedLeaseId,
        Guid expectedSessionId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        if (slotId == Guid.Empty || expectedLeaseId == Guid.Empty
            || expectedSessionId == Guid.Empty || expectedGeneration < 0)
        {
            throw new ArgumentException("An exact reserved slot selection is required.");
        }

        IReadOnlyList<WorkspaceReadySlotReservation> reserved =
            await _inventory.ListReservedAsync(cancellationToken);
        WorkspaceReadySlotReservation? selected = reserved.SingleOrDefault(
            item => item.Lease.SlotId == slotId
        );
        if (selected is null
            || selected.Lease.LeaseId != expectedLeaseId
            || selected.Session.OperationId != expectedSessionId
            || selected.Lease.Generation != expectedGeneration)
        {
            throw new InvalidOperationException(
                "The selected Reserved slot lease has changed; no files were modified."
            );
        }

        // Only supported GitHub PR identities can be resolved to a configured,
        // validated repository registration. Do not trust arbitrary journal
        // source paths as an authority for recovery.
        string key = selected.Session.RepositoryKey;
        if (!key.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
            || !PullRequestReference.TryParse(
                "https://github.com/" + key["github:".Length..]
                    + "/pull/" + selected.Session.PullRequestNumber,
                out PullRequestReference? request
            ) || request is null)
        {
            throw new InvalidOperationException(
                "The interrupted PR identity is not a trusted GitHub PR."
            );
        }

        RepositoryRegistration repository = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                "Configure the original source repository before recovering this checkout."
            );
        await _originVerifier.VerifyAsync(request, repository, cancellationToken);

        // The exact selected lease is checked again after acquiring the
        // interprocess slot lock, closing the UI-selection race.
        WorkspaceSession recovered = await _reconcile.ReconcileAsync(
            slotId, repository, cancellationToken,
            expectedLeaseId, expectedSessionId, expectedGeneration
        );

        // Editor failure does not reverse a successfully committed activation.
        await _editor.OpenAsync(recovered.WorkspacePath, cancellationToken);
        return recovered;
    }
}
