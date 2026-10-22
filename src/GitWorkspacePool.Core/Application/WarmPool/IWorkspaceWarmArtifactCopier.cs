using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Explicit opt-in copy of compatible, ignored artifacts. Never moves or deletes.
/// </summary>
public interface IWorkspaceWarmArtifactCopier
{
    /// <summary>Checks compatibility and copies into a never-before-existing destination.</summary>
    Task<WarmArtifactTransferReceipt> CopyAsync(
        WorkspaceSession releasedSource,
        WorkspaceSession activeDestination,
        CancellationToken cancellationToken
    );
}

/// <summary>Observed bytes and wall-clock copy cost, not an assumed speedup.</summary>
public sealed record WarmArtifactTransferReceipt(
    int FileCount,
    long CopiedBytes,
    TimeSpan Elapsed,
    string ArtifactRelativePath
);
