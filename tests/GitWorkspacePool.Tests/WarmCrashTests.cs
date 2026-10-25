using System.Diagnostics;
using GitWorkspacePool.Core.Domain;
using Xunit;

namespace GitWorkspacePool.Tests;

[Collection("Warm Git fixtures")]
public sealed class WarmCrashTests
{
    [Theory]
    [InlineData("after-intent")]
    [InlineData("after-checkout")]
    [InlineData("during-checkout")]
    public async Task KilledProcessLeavesDurableQuarantineWithoutRollback(string checkpoint)
    {
        using WarmWorkspaceTests.Fixture f = await WarmWorkspaceTests.Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        File.WriteAllText(Path.Combine(warm.Path, "retained.txt"), "crash evidence");
        await f.Commit("feature", "file.txt", "new target");
        if (checkpoint == "during-checkout")
        {
            // Large disposable checkout provides a reliable real index-lock kill window.
            string payload = new('x', 4096);
            for (int i = 0; i < 10000; i++) File.WriteAllText(Path.Combine(f.Source, $"kill-{i:D5}.txt"), payload);
            await f.Run("add", "."); await f.Run("commit", "-m", "large kill fixture target");
        }
        string target = await f.Run("rev-parse", "feature");
        string ready = Path.Combine(f.Root, "ready");
        string root = FindRepository();
        string configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string probe = Path.Combine(root, "tests", "GitWorkspacePool.CrashProbe", "bin", configuration, "net10.0", "GitWorkspacePool.CrashProbe.dll");
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { probe, "--warm-crash-fixture", f.Source, f.Repository.WorkspaceRoot, f.Database, warm.Id, target, checkpoint, ready }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        try
        {
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new InvalidOperationException(await error + await output);
                await Task.Delay(20, timeout.Token);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.OpenAsync(f.Repository, "feature", default));
            Assert.Equal(WarmWorkspaceState.Quarantined, f.Registry.List(f.Repository.RepositoryIdentity).Single().State);
            Assert.Equal("crash evidence", File.ReadAllText(Path.Combine(warm.Path, "retained.txt")));
            if (checkpoint == "during-checkout")
            {
                string admin = (await File.ReadAllTextAsync(Path.Combine(warm.Path, ".git"))).Trim()[8..];
                Assert.True(File.Exists(Path.Combine(admin, "index.lock")), "The Git checkout must still be interrupted, not merely completed before termination.");
            }
            else Assert.Equal(checkpoint == "after-checkout" ? target : warm.Head,
                await WarmWorkspaceTests.Fixture.GitAt(warm.Path, "rev-parse", "HEAD"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task RepositorySelectionWaitsForAnotherProcessAndContinuesAfterItsDeath()
    {
        using WarmWorkspaceTests.Fixture f = await WarmWorkspaceTests.Fixture.Create();
        WarmWorkspace donor = await f.Pool.PrepareAsync(f.Repository, "main", default);
        string configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string probe = Path.Combine(FindRepository(), "tests", "GitWorkspacePool.CrashProbe", "bin", configuration, "net10.0", "GitWorkspacePool.CrashProbe.dll");
        string ready = Path.Combine(f.Root, "lock-ready");
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false };
        foreach (string arg in new[] { probe, "--hold-lock-fixture", f.LockRoot, f.Source, ready }) start.ArgumentList.Add(arg);
        using Process holder = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(ready))
            {
                Assert.False(holder.HasExited); await Task.Delay(20, timeout.Token);
            }
            Task<WarmWorkspace> waiting = f.Pool.OpenAsync(f.Repository, "feature", timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(waiting.IsCompleted);
            holder.Kill(true); await holder.WaitForExitAsync(timeout.Token);
            Assert.Equal(donor.Path, (await waiting).Path);
        }
        finally { if (!holder.HasExited) { holder.Kill(true); await holder.WaitForExitAsync(); } }
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GitWorkspacePool.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root missing.");
    }
}
