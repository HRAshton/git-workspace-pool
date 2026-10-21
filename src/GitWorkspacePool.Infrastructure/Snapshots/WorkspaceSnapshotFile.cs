namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Metadata of a captured file or directory.
/// </summary>
/// <param name="RelativePath">Git-style forward-slash relative path.</param>
/// <param name="IsDirectory">Whether this is a directory.</param>
/// <param name="Sha256">Content hash for a file; null for a directory.</param>
/// <param name="Length">File length or zero for a directory.</param>
/// <param name="Attributes">Original filesystem attributes.</param>
/// <param name="LastWriteUtc">Original last write time.</param>
public sealed record WorkspaceSnapshotFile(
    string RelativePath,
    bool IsDirectory,
    string? Sha256,
    long Length,
    FileAttributes Attributes,
    DateTime LastWriteUtc
);
