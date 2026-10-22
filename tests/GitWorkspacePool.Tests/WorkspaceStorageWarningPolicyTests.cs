using GitWorkspacePool.Infrastructure;
using System.Text.Json;
using Xunit;

namespace GitWorkspacePool.Tests;

/// <summary>Advisory budgets never authorize eviction of snapshot or recovery data.</summary>
public sealed class WorkspaceStorageWarningPolicyTests
{
    [Fact]
    public void Assess_ReportsIndividualAndCombinedLogicalSizeWarnings()
    {
        WorkspaceLocalStorageUsage usage = new(
            new StorageAreaUsage(105, 2, 1),
            new StorageAreaUsage(80, 1, 1)
        );
        WorkspaceStorageWarningLimits limits = new()
        {
            MaxSnapshotBytes = 100,
            MaxRecoveredBytes = 80,
            MaxCombinedBytes = 180,
        };

        WorkspaceStorageWarningAssessment result =
            WorkspaceStorageWarningPolicy.Assess(usage, limits);

        Assert.True(result.AnyExceeded);
        Assert.True(result.SnapshotLimitExceeded);
        Assert.False(result.RecoveredLimitExceeded);
        Assert.True(result.CombinedLimitExceeded);
        Assert.Equal(185, result.TotalLogicalBytes);
    }

    [Fact]
    public void Assess_OmittedThresholdsDisableWarningsAndEqualityPasses()
    {
        WorkspaceLocalStorageUsage usage = new(
            new StorageAreaUsage(20, 1, 1),
            new StorageAreaUsage(30, 1, 1)
        );

        WorkspaceStorageWarningAssessment noLimits =
            WorkspaceStorageWarningPolicy.Assess(usage, new());
        Assert.False(noLimits.AnyExceeded);
        Assert.Equal(50, noLimits.TotalLogicalBytes);

        WorkspaceStorageWarningAssessment exact =
            WorkspaceStorageWarningPolicy.Assess(
                usage,
                new WorkspaceStorageWarningLimits
                {
                    MaxSnapshotBytes = 20,
                    MaxRecoveredBytes = 30,
                    MaxCombinedBytes = 50,
                }
            );
        Assert.False(exact.AnyExceeded);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(17592186044417L)]
    public void Validate_RejectsInvalidThresholdValues(long value)
    {
        Assert.Throws<InvalidOperationException>(
            () => new WorkspaceStorageWarningLimits { MaxSnapshotBytes = value }.Validate()
        );
        Assert.Throws<InvalidOperationException>(
            () => new WorkspaceStorageWarningLimits { MaxRecoveredBytes = value }.Validate()
        );
        Assert.Throws<InvalidOperationException>(
            () => new WorkspaceStorageWarningLimits { MaxCombinedBytes = value }.Validate()
        );
    }

    [Fact]
    public void Assess_RejectsNegativeOrOverflowedInventories()
    {
        WorkspaceStorageWarningLimits limits = new();
        Assert.Throws<InvalidOperationException>(
            () => WorkspaceStorageWarningPolicy.Assess(
                new(new(-1, 0, 0), new(0, 0, 0)), limits
            )
        );
        Assert.Throws<OverflowException>(
            () => WorkspaceStorageWarningPolicy.Assess(
                new(new(long.MaxValue, 1, 0), new(1, 1, 0)), limits
            )
        );
    }

    [Fact]
    public void SettingsAreOptionalAndStrictWhenDeserialized()
    {
        WorkspaceStorageWarningLimits defaults =
            JsonSerializer.Deserialize<WorkspaceStorageWarningLimits>("{}")!;
        Assert.False(WorkspaceStorageWarningPolicy.Assess(
            new(new(200, 2, 1), new(200, 2, 1)), defaults
        ).AnyExceeded);

        WorkspaceStorageWarningLimits configured =
            JsonSerializer.Deserialize<WorkspaceStorageWarningLimits>(
                "{\"MaxSnapshotBytes\":1024,\"MaxRecoveredBytes\":2048,\"MaxCombinedBytes\":3072}"
            )!;
        configured.Validate();
        Assert.Equal(1024, configured.MaxSnapshotBytes);

        Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Deserialize<WorkspaceStorageWarningLimits>(
                "{\"MaxCombinedBytes\":-12}"
            )!.Validate()
        );
    }
}
