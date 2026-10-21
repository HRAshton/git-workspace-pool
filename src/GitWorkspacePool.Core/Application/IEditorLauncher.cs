namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Opens an existing prepared directory in the user's configured editor.
/// </summary>
public interface IEditorLauncher
{
    /// <summary>
    /// Opens a directory without executing build commands.
    /// </summary>
    /// <param name="workspacePath">The prepared worktree path.</param>
    /// <param name="cancellationToken">Cancellation requested by the user.</param>
    /// <returns>The asynchronous launch operation.</returns>
    Task OpenAsync(string workspacePath, CancellationToken cancellationToken);
}
