using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Resolves browser trust, delegates ownership and launches the configured editor.</summary>
public sealed class OpenGitBranchHandler(IGitBranchCatalog catalog, WarmWorkspacePool pool, IEditorLauncher editor)
{
    /// <summary>Existing owners are opened unchanged, including dirty or locally advanced checkouts.</summary>
    public async Task<OpenWorkspaceResult> HandleAsync(GitBranchRequest request, CancellationToken cancellationToken)
    {
        if (!request.IsValid) throw new ArgumentException("Invalid Git clone URL or branch name.");
        GitRepositoryRegistration repository = catalog.Find(request)
            ?? throw new InvalidOperationException("Register this repository in Settings before opening a browser link.");
        if (!GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl))
            throw new InvalidOperationException("Registration does not match requested repository.");
        WarmWorkspace workspace = await pool.OpenAsync(repository, request.Branch, cancellationToken);
        await editor.OpenAsync(workspace.Path, cancellationToken);
        return new OpenWorkspaceResult(workspace.Path);
    }
}
