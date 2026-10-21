using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Opens a pull request without touching an existing worktree's checked-out files.
/// </summary>
public sealed class OpenPullRequestHandler
{
    private readonly IRepositoryCatalog _repositories;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceDirectories _directories;
    private readonly IEditorLauncher _editor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes the open-pull-request use case.
    /// </summary>
    public OpenPullRequestHandler(
        IRepositoryCatalog repositories,
        IWorkspaceGit git,
        IWorkspaceDirectories directories,
        IEditorLauncher editor
    )
    {
        _repositories = repositories;
        _git = git;
        _directories = directories;
        _editor = editor;
    }

    /// <summary>
    /// Opens a previously prepared PR or creates a new detached worktree.
    /// </summary>
    public async Task<OpenPullRequestResult> HandleAsync(
        string requestUrl,
        CancellationToken cancellationToken
    )
    {
        if (!PullRequestReference.TryParse(requestUrl, out PullRequestReference? request) || request is null)
        {
            throw new ArgumentException("Unsupported pull request URL.", nameof(requestUrl));
        }

        RepositoryRegistration registration = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                $"Register a local clone for {request.RepositoryKey} before opening this PR."
            );

        string folder = $"{request.Provider}-{request.Owner}-{request.Repository}-pr-{request.Number}";
        string workspacePath = Path.GetFullPath(Path.Combine(registration.WorkspaceRoot, folder));
        bool reused;

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_directories.Exists(workspacePath))
            {
                if (
                    !await _git.IsRegisteredWorktreeAsync(
                        registration.LocalPath,
                        workspacePath,
                        cancellationToken
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The target directory exists but is not a registered worktree. Nothing was changed."
                    );
                }

                reused = true;
            }
            else
            {
                _directories.EnsureRoot(registration.WorkspaceRoot);
                await _git.FetchPullRequestAsync(
                    registration.LocalPath,
                    request.Number,
                    cancellationToken
                );
                await _git.CreateWorktreeAsync(
                    registration.LocalPath,
                    workspacePath,
                    request.Number,
                    cancellationToken
                );
                reused = false;
            }
        }
        finally
        {
            _gate.Release();
        }

        await _editor.OpenAsync(workspacePath, cancellationToken);
        return new OpenPullRequestResult(workspacePath, reused);
    }
}

