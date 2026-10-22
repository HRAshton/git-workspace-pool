using System.Text.Json;
using GitWorkspacePool.Infrastructure.Snapshots;
using Xunit;

namespace GitWorkspacePool.Tests;

/// <summary>Configured limits must never bypass the tested snapshot ceilings.</summary>
public sealed class SnapshotExportLimitsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483649L)]
    public void InvalidByteLimitIsRejected(long bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SnapshotExportLimits { MaxBytes = bytes }.Validate()
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50001)]
    public void InvalidEntryLimitIsRejected(int entries)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SnapshotExportLimits { MaxEntries = entries }.Validate()
        );
    }

    [Fact]
    public void OmittedSettingsRetainExistingCeilings()
    {
        SnapshotExportLimits limits = JsonSerializer.Deserialize<SnapshotExportLimits>("{}")!;
        limits.Validate();
        Assert.Equal(2147483648L, limits.MaxBytes);
        Assert.Equal(50000, limits.MaxEntries);
    }
}
