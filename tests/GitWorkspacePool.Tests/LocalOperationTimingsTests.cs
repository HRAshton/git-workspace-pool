using System.Text.Json;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Diagnostics;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class LocalOperationTimingsTests
{
    [Fact]
    public void DiagnosticsAreCreatedOnlyWhenRecordedAndContainOnlyFixedFields()
    {
        using Fixture fixture = new();
        LocalOperationTimings timings = new(fixture.Path);
        Assert.Empty(timings.Read().Samples);
        Assert.False(File.Exists(fixture.Path));
        timings.Record(WorkspaceOperation.OpenBranch, TimeSpan.FromMilliseconds(125), WorkspaceOperationOutcome.Completed);
        WorkspaceTimingSample sample = Assert.Single(timings.Read().Samples);
        Assert.Equal(WorkspaceOperation.OpenBranch, sample.Operation);
        Assert.Equal(125, sample.ElapsedMilliseconds);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(new[] { "Timestamp", "Operation", "ElapsedMilliseconds", "Outcome" },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void DamagedTimingRecordsAreCountedAndValidSamplesRemainReadable()
    {
        using Fixture fixture = new();
        LocalOperationTimings timings = new(fixture.Path);
        timings.Record(WorkspaceOperation.Park, TimeSpan.FromSeconds(1), WorkspaceOperationOutcome.Completed);
        File.AppendAllText(fixture.Path, "{invalid}\n{\"Operation\":9999,\"Outcome\":0,\"ElapsedMilliseconds\":2}\n");
        var report = timings.Read();
        Assert.Single(report.Samples);
        Assert.Equal(2, report.UnreadableRecords);
    }

    [Fact]
    public void DiagnosticRotationRetainsPreviousDataAndBoundsTheCurrentFile()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        File.WriteAllText(fixture.Path, new string('x', 1024 * 1024));
        LocalOperationTimings timings = new(fixture.Path);
        timings.Record(WorkspaceOperation.Snapshot, TimeSpan.FromSeconds(2), WorkspaceOperationOutcome.Cancelled);
        Assert.True(new FileInfo(fixture.Path).Length < 1024 * 1024);
        Assert.Equal(1024 * 1024, new FileInfo(fixture.Path + ".previous").Length);
        Assert.Equal(WorkspaceOperationOutcome.Cancelled, Assert.Single(timings.Read().Samples).Outcome);
    }

    [Fact]
    public void PercentilesUseNearestRankAndDoNotPretendEmptySamplesAreMeasurements()
    {
        Assert.Equal(30, LocalOperationTimings.Percentile([50, 10, 40, 20, 30], 0.5));
        Assert.Equal(50, LocalOperationTimings.Percentile([50, 10, 40, 20, 30], 0.95));
        Assert.Equal(0, LocalOperationTimings.Percentile([], 0.95));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalOperationTimings.Percentile([1], double.NaN));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gwp-timings-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "diagnostics", "timings.jsonl");
        public Fixture() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
