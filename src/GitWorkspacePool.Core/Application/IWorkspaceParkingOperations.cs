namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Narrow Git and filesystem boundary for relocating a whole linked worktree.
/// </summary>
public interface IWorkspaceParkingOperations
{
    /// <summary>Checks whether a destination is already occupied.</summary>
    bool Exists(string path);

    /// <summary>Validates the destination's parent and creates only the parking root.</summary>
    void PrepareParkingDirectory(string original, string destination);

    /// <summary>Queries actual linked-worktree Git registration.</summary>
    Task<bool> IsRegisteredAsync(string repository, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Refuses unsupported Git submodule index entries before a move intent is persisted.
    /// </summary>
    Task EnsureMoveSupportedAsync(string worktree, CancellationToken cancellationToken);

    /// <summary>Reads the actual current HEAD commit.</summary>
    Task<string> GetHeadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Moves a linked worktree with Git (not raw Directory.Move).</summary>
    Task MoveAsync(string repository, string source, string destination, CancellationToken cancellationToken);
}
