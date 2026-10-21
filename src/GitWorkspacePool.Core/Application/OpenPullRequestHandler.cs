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

    /// <summary>
    /// Initializes the PR-opening use case.
    /// </summary>
    public OpenPullRequestHandler(
        IRepositoryCatalog repositories,
        IRepositoryOriginVerifier originVerifier,
        WorkspaceAcquisitionService acquisition,
        IEditorLauncher editor
    )
    {
        _repositories = repositories;
        _originVerifier = originVerifier;
        _acquisition = acquisition;
        _editor = editor;
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

        OpenPullRequestResult result = await _acquisition.AcquireAsync(
            request,
            registration,
            cancellationToken
        );

        await _editor.OpenAsync(result.WorkspacePath, cancellationToken);
        return result;
    }
}
