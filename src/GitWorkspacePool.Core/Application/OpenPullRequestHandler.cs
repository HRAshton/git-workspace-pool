using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Coordinates request validation, repository trust, workspace allocation and editor launch.
/// </summary>
public sealed class OpenPullRequestHandler
{
    private readonly IRepositoryCatalog _repositories;
    private readonly IRepositoryOriginVerifier _originVerifier;
    private readonly WorkspaceAcquisitionService _acquisition;
    private readonly IEditorLauncher _editor;
    private readonly IWorkspaceSessionJournal? _sessions;
    private readonly OpenReleasedParkedWorkspaceService? _retained;

    /// <summary>
    /// Initializes the PR-opening use case.
    /// </summary>
    public OpenPullRequestHandler(
        IRepositoryCatalog repositories,
        IRepositoryOriginVerifier originVerifier,
        WorkspaceAcquisitionService acquisition,
        IEditorLauncher editor,
        IWorkspaceSessionJournal? sessions = null,
        OpenReleasedParkedWorkspaceService? retained = null
    )
    {
        _repositories = repositories;
        _originVerifier = originVerifier;
        if ((sessions is null) != (retained is null))
        {
            throw new ArgumentException(
                "Released PR reopening requires both the trusted journal and verifier."
            );
        }

        _acquisition = acquisition;
        _editor = editor;
        _sessions = sessions;
        _retained = retained;
    }

    /// <summary>
    /// Opens an existing or safely prepared PR workspace in the configured editor.
    /// </summary>
    public async Task<OpenPullRequestResult> HandleAsync(
        string requestUrl,
        CancellationToken cancellationToken
    )
    {
        if (
            !PullRequestReference.TryParse(requestUrl, out PullRequestReference? request)
            || request is null
        )
        {
            throw new ArgumentException("Unsupported pull request URL.", nameof(requestUrl));
        }

        RepositoryRegistration registration = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                $"Register a local clone for {request.RepositoryKey} before opening this PR."
            );

        await _originVerifier.VerifyAsync(request, registration, cancellationToken);

        // Released PRs retain their original checked-out data in private parking.
        // A browser PR-open request is already an explicit editor-open action.
        // Trust and root checks precede the same locked Git verification used
        // by the tray, without a new session, checkout or former-slot lease.
        if (_sessions is not null)
        {
            WorkspaceSession? existing = await _sessions.FindByPullRequestAsync(
                request.Provider + ":" + request.RepositoryKey,
                request.Number,
                cancellationToken
            );
            if (existing?.State == WorkspaceSessionState.Released)
            {
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (
                    !Path.GetFullPath(existing.SourcePath).Equals(
                        Path.GetFullPath(registration.LocalPath), comparison
                    )
                    || !Path.GetDirectoryName(Path.GetFullPath(existing.WorkspacePath))!.Equals(
                        Path.GetFullPath(registration.WorkspaceRoot), comparison
                    )
                )
                {
                    throw new InvalidOperationException(
                        "Released PR ownership does not match the trusted configured repository."
                    );
                }

                string parked = await _retained!.OpenAsync(
                    existing.OperationId, cancellationToken
                );
                return new OpenPullRequestResult(parked, true);
            }
        }

        OpenPullRequestResult result = await _acquisition.AcquireAsync(
            request,
            registration,
            cancellationToken
        );

        await _editor.OpenAsync(result.WorkspacePath, cancellationToken);
        return result;
    }
}
