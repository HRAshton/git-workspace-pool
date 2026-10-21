namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Provides the workspace directory operations needed by application use cases.
/// </summary>
public interface IWorkspaceDirectories
{
    /// <summary>
    /// Checks if a directory or file exists at the specified path.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>Whether the path is occupied.</returns>
    bool Exists(string path);

    /// <summary>
    /// Ensures an explicitly configured root exists.
    /// </summary>
    /// <param name="root">The configured root directory.</param>
    void EnsureRoot(string root);
}
