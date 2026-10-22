namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Per-export resource limits. Settings may reduce the tested safety ceilings.
/// These limits do not authorize retention cleanup or constrain existing restores.
/// </summary>
public sealed record SnapshotExportLimits
{
    /// <summary>Gets the maximum combined index, staged and working payload bytes.</summary>
    public long MaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Gets the maximum changed paths and copied payload entries.</summary>
    public int MaxEntries { get; init; } = 50000;

    /// <summary>Rejects invalid limits before scanning or creating snapshot storage.</summary>
    public void Validate()
    {
        if (MaxBytes is < 1 or > 2L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBytes), "Snapshot MaxBytes must be between 1 and 2147483648."
            );
        }

        if (MaxEntries is < 1 or > 50000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEntries), "Snapshot MaxEntries must be between 1 and 50000."
            );
        }
    }
}
