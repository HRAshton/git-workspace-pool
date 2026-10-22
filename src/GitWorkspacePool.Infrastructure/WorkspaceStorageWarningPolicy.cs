namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Optional advisory warning thresholds for logical bytes in app-owned evidence stores.
/// Never a deletion or eviction policy; null thresholds disable the related warning.
/// </summary>
public sealed class WorkspaceStorageWarningLimits
{
    private const long MaximumThreshold = 16L * 1024 * 1024 * 1024 * 1024;

    /// <summary>Optional snapshot-store logical byte warning threshold.</summary>
    public long? MaxSnapshotBytes { get; set; }

    /// <summary>Optional isolated recovery-store logical byte warning threshold.</summary>
    public long? MaxRecoveredBytes { get; set; }

    /// <summary>Optional combined logical byte warning threshold.</summary>
    public long? MaxCombinedBytes { get; set; }

    /// <summary>Rejects invalid settings before opening or scanning workspace evidence.</summary>
    public void Validate()
    {
        ValidateOne(MaxSnapshotBytes, nameof(MaxSnapshotBytes));
        ValidateOne(MaxRecoveredBytes, nameof(MaxRecoveredBytes));
        ValidateOne(MaxCombinedBytes, nameof(MaxCombinedBytes));
    }

    private static void ValidateOne(long? value, string field)
    {
        if (value is <= 0 or > MaximumThreshold)
        {
            throw new InvalidOperationException(
                $"Storage warning limit {field} must be between 1 and 16 TiB, or null."
            );
        }
    }
}

/// <summary>
/// Advisory result computed only from a completed, verified evidence inventory.
/// This result never indicates which file is safe to delete or release.
/// </summary>
public sealed record WorkspaceStorageWarningAssessment(
    long TotalLogicalBytes,
    bool SnapshotLimitExceeded,
    bool RecoveredLimitExceeded,
    bool CombinedLimitExceeded
)
{
    /// <summary>True when any configured logical byte warning threshold was exceeded.</summary>
    public bool AnyExceeded =>
        SnapshotLimitExceeded || RecoveredLimitExceeded || CombinedLimitExceeded;
}

/// <summary>Pure non-destructive policy for advisory storage threshold warnings.</summary>
public static class WorkspaceStorageWarningPolicy
{
    /// <summary>
    /// Compares bounded logical evidence usage with user-configured warning levels.
    /// Equal sizes do not exceed a warning; no implicit cleanup is performed.
    /// </summary>
    public static WorkspaceStorageWarningAssessment Assess(
        WorkspaceLocalStorageUsage usage,
        WorkspaceStorageWarningLimits limits
    )
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        if (usage.Snapshots.Bytes < 0 || usage.Recovered.Bytes < 0)
        {
            throw new InvalidOperationException("Storage usage cannot be negative.");
        }

        long total = checked(usage.Snapshots.Bytes + usage.Recovered.Bytes);
        return new WorkspaceStorageWarningAssessment(
            total,
            limits.MaxSnapshotBytes is long snapshot && usage.Snapshots.Bytes > snapshot,
            limits.MaxRecoveredBytes is long recovered && usage.Recovered.Bytes > recovered,
            limits.MaxCombinedBytes is long combined && total > combined
        );
    }
}
