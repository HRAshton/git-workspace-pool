using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Trust-checked, explicitly selected Ready-slot PR opener for user-facing UI.
/// No implicit candidate selection or automatic eviction is performed.
/// </summary>
public sealed class OpenPullRequestInReadySlotHandler
{
    private readonly IRepositoryCatalog _repositories;
    private readonly IRepositoryOriginVerifier _originVerifier;
    private readonly AcquireExplicitReadySlotService _acquisition;
    private readonly IEditorLauncher _editor;

    /// <summary>Builds a trusted, explicitly selected PR-opening use case.</summary>
    public OpenPullRequestInReadySlotHandler(
        IRepositoryCatalog repositories,
        IRepositoryOriginVerifier originVerifier,
        AcquireExplicitReadySlotService acquisition,
        IEditorLauncher editor
    )
    {
        _repositories = repositories;
        _originVerifier = originVerifier;
        _acquisition = acquisition;
        _editor = editor;
    }

    /// <summary>
    /// Verifies the URL, registered repository and Git origin before reserving
    /// the specifically selected slot. Launches the editor only after success.
    /// </summary>
    public async Task<OpenWorkspaceResult> HandleAsync(
        string requestUrl,
        Guid slotId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        if (
            !PullRequestReference.TryParse(requestUrl, out PullRequestReference? request)
            || request is null
        )
        {
            throw new ArgumentException("Unsupported GitHub pull request URL.", nameof(requestUrl));
        }

        RepositoryRegistration registration = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                "Register a trusted local clone for " + request.RepositoryKey
                    + " before allocating a released slot."
            );

        await _originVerifier.VerifyAsync(request, registration, cancellationToken);
        OpenWorkspaceResult result = await _acquisition.AcquireAsync(
            request, registration, slotId, expectedGeneration, cancellationToken
        );
        await _editor.OpenAsync(result.WorkspacePath, cancellationToken);
        return result;
    }
}
