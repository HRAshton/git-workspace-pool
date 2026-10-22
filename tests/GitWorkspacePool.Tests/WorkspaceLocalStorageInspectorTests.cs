using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests;

/// <summary>Read-only app-owned evidence accounting must never authorize cleanup.</summary>
public sealed class WorkspaceLocalStorageInspectorTests
{
    [Fact]
    public void Inspect_CountsSnapshotsAndRestoresWithoutMutatingEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "gwp-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            string snapshots = Path.Combine(root, "snapshots", "owned", "snapshot-one");
            string restored = Path.Combine(root, "recovered", "restore-one");
            Directory.CreateDirectory(snapshots);
            Directory.CreateDirectory(restored);
            string manifest = Path.Combine(snapshots, "manifest.json");
            string index = Path.Combine(snapshots, "index.bin");
            string file = Path.Combine(restored, "feature.txt");
            File.WriteAllBytes(manifest, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(index, new byte[] { 4, 5 });
            File.WriteAllBytes(file, new byte[] { 6, 7, 8, 9 });

            WorkspaceLocalStorageUsage usage = new WorkspaceLocalStorageInspector(root)
                .Inspect(CancellationToken.None);

            Assert.Equal(5, usage.Snapshots.Bytes);
            Assert.Equal(2, usage.Snapshots.Files);
            Assert.Equal(2, usage.Snapshots.Directories);
            Assert.Equal(4, usage.Recovered.Bytes);
            Assert.Single(File.ReadAllBytes(file).Chunk(4));
            Assert.Equal(3, File.ReadAllBytes(manifest).Length);
            Assert.True(File.Exists(index));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Inspect_MissingStoresAreEmptyAndCancelledScanRefuses()
    {
        string root = Path.Combine(Path.GetTempPath(), "gwp-usage-" + Guid.NewGuid().ToString("N"));
        WorkspaceLocalStorageInspector inspector = new(root);
        WorkspaceLocalStorageUsage usage = inspector.Inspect(CancellationToken.None);

        Assert.Equal(0, usage.Snapshots.Bytes);
        Assert.Equal(0, usage.Recovered.Bytes);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => inspector.Inspect(cancelled.Token));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Inspect_RefusesExcessEntriesAndUnexpectedFileRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "gwp-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            string snapshots = Path.Combine(root, "snapshots");
            Directory.CreateDirectory(snapshots);
            File.WriteAllText(Path.Combine(snapshots, "one"), "a");
            File.WriteAllText(Path.Combine(snapshots, "two"), "b");
            File.WriteAllText(Path.Combine(snapshots, "three"), "c");

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => new WorkspaceLocalStorageInspector(root, maxEntries: 2)
                    .Inspect(CancellationToken.None)
            );
            Assert.Contains("bounded entry limit", failure.Message);
            Directory.Delete(snapshots, recursive: true);
            File.WriteAllText(snapshots, "foreign occupant");

            failure = Assert.Throws<InvalidOperationException>(
                () => new WorkspaceLocalStorageInspector(root).Inspect(CancellationToken.None)
            );
            Assert.Contains("usage is unknown", failure.Message);
            Assert.Equal("foreign occupant", File.ReadAllText(snapshots));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Inspector_RefusesInvalidLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceLocalStorageInspector(Path.GetTempPath(), maxEntries: 0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkspaceLocalStorageInspector(Path.GetTempPath(), maxEntries: 100001)
        );
    }
}
