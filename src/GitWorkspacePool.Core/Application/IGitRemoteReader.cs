namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Reads the origin identity of an explicitly configured local Git clone.
/// </summary>
public interface IGitRemoteReader
{
    /// <summary>
    /// Resolves the origin URL using Git's own URL-rewrite configuration.
    /// </summary>
    /// <param name="repositoryPath">The configured local clone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved Git origin URL.</returns>
    Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken);
}
