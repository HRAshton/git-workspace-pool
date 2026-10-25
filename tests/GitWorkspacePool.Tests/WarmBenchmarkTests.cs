using System.Diagnostics;
using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests;

[Collection("Warm Git fixtures")]
public sealed class WarmBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SyntheticLargeTreeReportsFreshWarmAndReopenDistributions()
    {
        using WarmWorkspaceTests.Fixture f = await WarmWorkspaceTests.Fixture.Create();
        const int files = 10000;
        const int changed = 10;
        const int artifactBytes = 16 * 1024 * 1024;
        for (int i = 0; i < files; i++) File.WriteAllText(Path.Combine(f.Source, $"tracked-{i:D5}.txt"), "tracked fixture content\n");
        await f.Run("add", "."); await f.Run("commit", "-m", "synthetic large tree");
        await f.Run("branch", "--force", "feature");
        await f.Run("switch", "feature");
        for (int i = 0; i < changed; i++) File.WriteAllText(Path.Combine(f.Source, $"tracked-{i:D5}.txt"), "changed content\n");
        await f.Run("commit", "-am", "small tree delta");
        List<object> samples = [];
        List<double> freshTimes = []; List<double> warmTimes = []; List<double> reopenTimes = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            WarmWorkspacePool Make(string lane)
            {
                string root = Path.Combine(f.Root, lane + iteration);
                return new(new SqliteWarmWorkspaceRegistry(Path.Combine(root, "workspaces.db")), f.Git, new FileWorkspaceOperationLock(f.LockRoot));
            }
            GitRepositoryRegistration coldRepository = f.Repository with { WorkspaceRoot = Path.Combine(f.Root, "fresh" + iteration) };
            WarmWorkspacePool cold = Make("cold-state");
            Stopwatch timer = Stopwatch.StartNew();
            WarmWorkspace fresh = await cold.OpenAsync(coldRepository, "feature", default);
            double freshMs = timer.Elapsed.TotalMilliseconds;
            GitRepositoryRegistration warmRepository = f.Repository with { WorkspaceRoot = Path.Combine(f.Root, "warm" + iteration) };
            WarmWorkspacePool pool = Make("warm-state");
            WarmWorkspace donor = await pool.PrepareAsync(warmRepository, "main", default);
            string artifact = Path.Combine(donor.Path, "cache", "payload");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllBytes(artifact, new byte[artifactBytes]);
            Dictionary<string, DateTime> timestamps = Directory.EnumerateFiles(donor.Path, "tracked-*.txt")
                .ToDictionary(path => path, File.GetLastWriteTimeUtc);
            // Give filesystem timestamp resolution room; fixture setup is outside measured intervals.
            await Task.Delay(30);
            timer.Restart();
            WarmWorkspace result = await pool.OpenAsync(warmRepository, "feature", default);
            double warmMs = timer.Elapsed.TotalMilliseconds;
            int observedWrites = timestamps.Count(pair => File.GetLastWriteTimeUtc(pair.Key) != pair.Value);
            Assert.Equal(donor.Path, result.Path);
            Assert.Equal(artifactBytes, new FileInfo(artifact).Length);
            Assert.InRange(observedWrites, changed, changed);
            timer.Restart();
            await pool.OpenAsync(warmRepository, "feature", default);
            double reopenMs = timer.Elapsed.TotalMilliseconds;
            freshTimes.Add(freshMs); warmTimes.Add(warmMs); reopenTimes.Add(reopenMs);
            samples.Add(new { iteration, freshMs, warmMs, reopenMs, trackedFiles = files + 2, treeDelta = changed,
                warmTrackedFilesWithNewMtime = observedWrites, freshTrackedFilesCreated = files + 2, preservedArtifactBytes = artifactBytes });
        }
        static object Distribution(List<double> times)
        {
            double[] sorted = times.Order().ToArray();
            return new { medianMs = sorted[sorted.Length / 2], p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] };
        }
        string json = JsonSerializer.Serialize(new { workload = "synthetic disposable 10000-file tree, 10-path delta, 16MiB ignored artifact; not browser/editor or representative real repository evidence",
            samples, fresh = Distribution(freshTimes), warm = Distribution(warmTimes), reopen = Distribution(reopenTimes) }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        string? destination = Environment.GetEnvironmentVariable("GWP_BENCHMARK_REPORT");
        if (destination is not null) await File.WriteAllTextAsync(destination, json);
    }
}
