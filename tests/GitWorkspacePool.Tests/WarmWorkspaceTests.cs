using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitWorkspacePool.Tests;

[CollectionDefinition("Warm Git fixtures", DisableParallelization = true)]
public sealed class WarmGitCollection { }

[Collection("Warm Git fixtures")]
public sealed class WarmWorkspaceTests
{
    [Fact]
    public async Task ExactHeadAssignsExistingDirectoryAndRetainsArtifacts()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        string artifact = Path.Combine(warm.Path, "cache", "sentinel");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "do not copy or delete");
        DateTime created = Directory.GetCreationTimeUtc(warm.Path);
        WarmWorkspace assigned = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.Equal(warm.Path, assigned.Path);
        Assert.Equal(created, Directory.GetCreationTimeUtc(assigned.Path));
        Assert.Equal("do not copy or delete", File.ReadAllText(artifact));
        Assert.Single(f.Registry.List(f.Repository.RepositoryIdentity));
        Assert.Equal(0, f.Git.Transitions);
    }

    [Fact]
    public async Task NearbyDonorSwitchesInPlaceAndPreservesIgnoredBytes()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        File.WriteAllText(Path.Combine(warm.Path, "retained.txt"), "ignored bytes");
        await f.Commit("feature", "file.txt", "changed");
        WarmWorkspace result = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.Equal(warm.Path, result.Path);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(result.Path, "file.txt")));
        Assert.Equal("ignored bytes", File.ReadAllText(Path.Combine(result.Path, "retained.txt")));
        Assert.Equal(1, f.Git.Transitions);
        Assert.Equal(await f.Run("rev-parse", "feature"), result.Head);
    }

    [Fact]
    public async Task AssignedReopenPreservesLocalCommitsStagingDirtyAndUntrackedWithoutFetch()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace assigned = await f.Pool.OpenAsync(f.Repository, "feature", default);
        await Fixture.GitAt(assigned.Path, "config", "user.name", "Fixture");
        await Fixture.GitAt(assigned.Path, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(assigned.Path, "file.txt"), "local commit");
        await Fixture.GitAt(assigned.Path, "add", "file.txt");
        await Fixture.GitAt(assigned.Path, "commit", "-m", "local detached work");
        string localHead = await Fixture.GitAt(assigned.Path, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(assigned.Path, "file.txt"), "staged");
        await Fixture.GitAt(assigned.Path, "add", "file.txt");
        File.AppendAllText(Path.Combine(assigned.Path, "file.txt"), " plus unstaged");
        File.WriteAllText(Path.Combine(assigned.Path, "untracked"), "user work");
        File.WriteAllText(Path.Combine(assigned.Path, "retained.txt"), "cache");
        string status = await Fixture.GitAt(assigned.Path, "status", "--porcelain");
        int fetches = f.Git.Fetches;
        Assert.Equal(assigned.Path, (await f.Pool.OpenAsync(f.Repository, "feature", default)).Path);
        Assert.Equal(fetches, f.Git.Fetches);
        Assert.Equal(localHead, await Fixture.GitAt(assigned.Path, "rev-parse", "HEAD"));
        Assert.Equal(status, await Fixture.GitAt(assigned.Path, "status", "--porcelain"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.ReleaseAsync(f.Repository, assigned.Id, default));
        Assert.Equal(WarmWorkspaceState.Assigned, f.Registry.List(f.Repository.RepositoryIdentity).Single().State);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("assume-unchanged")]
    [InlineData("local-branch")]
    [InlineData("index.lock")]
    [InlineData("registration-lock")]
    [InlineData("local-commit")]
    public async Task UnsafeDonorIsQuarantinedAndFreshFallbackPreservesIt(string kind)
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        if (kind is "dirty" or "staged") File.WriteAllText(Path.Combine(warm.Path, "file.txt"), "user bytes");
        if (kind == "staged") await Fixture.GitAt(warm.Path, "add", "file.txt");
        if (kind == "untracked") File.WriteAllText(Path.Combine(warm.Path, "user"), "user bytes");
        if (kind == "assume-unchanged") await Fixture.GitAt(warm.Path, "update-index", "--assume-unchanged", "file.txt");
        if (kind == "local-branch") await Fixture.GitAt(warm.Path, "switch", "-c", "user-branch");
        if (kind == "local-commit")
        {
            File.WriteAllText(Path.Combine(warm.Path, "file.txt"), "protected commit");
            await Fixture.GitAt(warm.Path, "commit", "-am", "user detached commit");
        }
        if (kind == "registration-lock") await f.Run("worktree", "lock", "--reason", "user lock", warm.Path);
        if (kind == "index.lock")
        {
            string admin = await Fixture.GitAt(warm.Path, "rev-parse", "--absolute-git-dir");
            File.WriteAllText(Path.Combine(admin, "index.lock"), "evidence");
        }
        WarmWorkspace assigned = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.NotEqual(warm.Path, assigned.Path);
        Assert.Equal(WarmWorkspaceState.Quarantined, f.Registry.List(f.Repository.RepositoryIdentity).Single(row => row.Id == warm.Id).State);
        Assert.True(Directory.Exists(warm.Path));
        if (kind is "dirty" or "staged") Assert.Equal("user bytes", File.ReadAllText(Path.Combine(warm.Path, "file.txt")));
    }

    [Fact]
    public async Task IncomingTrackedArtifactCollisionIsRejectedBeforeMutation()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        File.WriteAllText(Path.Combine(warm.Path, "retained.txt"), "important ignored bytes");
        await f.Run("switch", "feature");
        File.WriteAllText(Path.Combine(f.Source, "retained.txt"), "incoming tracked");
        await f.Run("add", "--force", "retained.txt");
        await f.Run("commit", "-m", "track formerly ignored file");
        WarmWorkspace result = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.NotEqual(warm.Path, result.Path);
        Assert.Equal("important ignored bytes", File.ReadAllText(Path.Combine(warm.Path, "retained.txt")));
        Assert.Equal(0, f.Git.Transitions);
    }

    [Fact]
    public async Task TreeDeltaWinsOverCommitCountAndArtifactPolicyRejectsMismatch()
    {
        using Fixture f = await Fixture.Create();
        await f.Run("branch", "baseline");
        WarmWorkspace exactTree = await f.Pool.PrepareAsync(f.Repository, "main", default);
        for (int i = 0; i < 12; i++) await f.Run("commit", "--allow-empty", "-m", "history only");
        await f.Run("branch", "long-history");
        await f.Commit("baseline", "file.txt", "larger tree delta");
        WarmWorkspace farther = await f.Pool.PrepareAsync(f.Repository, "baseline", default);
        Assert.Equal(exactTree.Path, (await f.Pool.OpenAsync(f.Repository, "long-history", default)).Path);
        Assert.Equal(WarmWorkspaceState.Available, f.Registry.List(f.Repository.RepositoryIdentity).Single(row => row.Id == farther.Id).State);

        using Fixture policy = await Fixture.Create();
        GitRepositoryRegistration configured = policy.Repository with { ArtifactInputs = new[] { "file.txt" } };
        WarmWorkspace incompatible = await policy.Pool.PrepareAsync(configured, "main", default);
        await policy.Commit("feature", "file.txt", "dependency changed");
        WarmWorkspace compatible = await policy.Pool.PrepareAsync(configured, "feature", default);
        Assert.Equal(compatible.Path, (await policy.Pool.OpenAsync(configured, "feature", default)).Path);
        Assert.Equal(WarmWorkspaceState.Available, policy.Registry.List(configured.RepositoryIdentity).Single(row => row.Id == incompatible.Id).State);
    }

    [Fact]
    public async Task ReleaseIsExplicitAndDoesNotMoveOrClean()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace owner = await f.Pool.OpenAsync(f.Repository, "feature", default);
        File.WriteAllText(Path.Combine(owner.Path, "retained.txt"), "cache");
        await f.Pool.ReleaseAsync(f.Repository, owner.Id, default);
        Assert.Equal(WarmWorkspaceState.Available, f.Registry.List(f.Repository.RepositoryIdentity).Single().State);
        Assert.Equal(owner.Path, (await f.Pool.OpenAsync(f.Repository, "main", default)).Path);
        Assert.Equal("cache", File.ReadAllText(Path.Combine(owner.Path, "retained.txt")));
    }

    [Fact]
    public async Task ConcurrentPoolInstancesCannotDoubleClaimDonor()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        WarmWorkspacePool second = new(new SqliteWarmWorkspaceRegistry(f.Database), f.Git, new FileWorkspaceOperationLock(f.LockRoot));
        WarmWorkspace[] results = await Task.WhenAll(f.Pool.OpenAsync(f.Repository, "main", default), second.OpenAsync(f.Repository, "feature", default));
        Assert.NotEqual(results[0].Path, results[1].Path);
        Assert.Single(results.Where(row => row.Path == warm.Path));
        Assert.Equal(2, f.Registry.List(f.Repository.RepositoryIdentity).Count);
    }

    [Fact]
    public async Task LeftoverIntentAndFailedCheckoutRetainBytesAndNeverAutoResume()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        File.WriteAllText(Path.Combine(warm.Path, "retained.txt"), "evidence");
        f.Registry.BeginTransition(warm.Id, "feature", warm.Head);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.OpenAsync(f.Repository, "feature", default));
        Assert.Equal(WarmWorkspaceState.Quarantined, f.Registry.List(f.Repository.RepositoryIdentity).Single().State);
        Assert.Equal("evidence", File.ReadAllText(Path.Combine(warm.Path, "retained.txt")));

        using Fixture failure = await Fixture.Create();
        WarmWorkspace donor = await failure.Pool.PrepareAsync(failure.Repository, "main", default);
        await failure.Commit("feature", "file.txt", "new target");
        failure.Git.FailCheckout = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure.Pool.OpenAsync(failure.Repository, "feature", default));
        Assert.Equal(WarmWorkspaceState.Quarantined, failure.Registry.List(failure.Repository.RepositoryIdentity).Single().State);
        Assert.True(Directory.Exists(donor.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-origin")]
    [InlineData("filter")]
    [InlineData("sparse")]
    public async Task UncertainIdentityOrExecutionSemanticsFailClosed(string kind)
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace assigned = await f.Pool.OpenAsync(f.Repository, "feature", default);
        if (kind == "missing") Directory.Move(assigned.Path, assigned.Path + "-moved");
        if (kind == "wrong-origin") await f.Run("remote", "set-url", "origin", "https://foreign.invalid/repo.git");
        if (kind == "filter") await f.Run("config", "filter.hostile.smudge", "echo executed");
        if (kind == "sparse") await f.Run("config", "core.sparseCheckout", "true");
        if (kind is "missing" or "wrong-origin")
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.OpenAsync(f.Repository, "feature", default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.ReleaseAsync(f.Repository, assigned.Id, default));
    }

    [Fact]
    public async Task EditorFailureLeavesValidAssignmentAndProtocolRejectsUnknownRepository()
    {
        using Fixture f = await Fixture.Create();
        OpenGitBranchHandler handler = new(new Catalog(f.Repository), f.Pool, new BrokenEditor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new(f.Repository.CloneUrl, "feature"), default));
        Assert.Equal(WarmWorkspaceState.Assigned, f.Registry.List(f.Repository.RepositoryIdentity).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new("https://unknown.invalid/repo.git", "feature"), default));
        Assert.Single(f.Registry.List(f.Repository.RepositoryIdentity));
    }

    [Fact]
    public async Task DatabaseRejectsForeignSchemasAndEnforcesUniqueOwnership()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace row = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.Throws<SqliteException>(() => f.Registry.InsertIntent(row with { Id = "duplicate", State = WarmWorkspaceState.Transitioning, Target = row.Head }));
        Assert.Throws<SqliteException>(() => f.Registry.InsertIntent(row with { Id = "duplicate", Path = row.Path + "-new", State = WarmWorkspaceState.Transitioning, Target = row.Head }));
        string foreignRoot = Path.Combine(f.Root, "foreign"); Directory.CreateDirectory(foreignRoot);
        string foreign = Path.Combine(foreignRoot, "workspaces.db");
        File.WriteAllText(foreign, "legacy evidence");
        Assert.ThrowsAny<Exception>(() => new SqliteWarmWorkspaceRegistry(foreign));
        Assert.Equal("legacy evidence", File.ReadAllText(foreign));
        Assert.Throws<ArgumentException>(() => new SqliteWarmWorkspaceRegistry(Path.Combine(f.Root, "sessions.db")));
    }

    [Fact]
    public async Task RootLinksAndIncomingCacheLinksFailClosedWithoutFollowingTargets()
    {
        using Fixture f = await Fixture.Create();
        WarmWorkspace warm = await f.Pool.PrepareAsync(f.Repository, "main", default);
        string external = Path.Combine(f.Root, "external"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep"), "foreign bytes");
        string link = Path.Combine(warm.Path, "cache");
        if (OperatingSystem.IsWindows())
        {
            ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, external }) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, external);
        // Untouched ignored links are supported without traversal.
        await new WarmWorktreeGit().VerifyAvailableAsync(f.Repository, warm, warm.Head, default);
        await f.Run("switch", "feature");
        Directory.CreateDirectory(Path.Combine(f.Source, "cache"));
        File.WriteAllText(Path.Combine(f.Source, "cache", "new-file"), "target");
        await f.Run("add", "--force", "cache/new-file"); await f.Run("commit", "-m", "incoming cache collision");
        WarmWorkspace result = await f.Pool.OpenAsync(f.Repository, "feature", default);
        Assert.NotEqual(warm.Path, result.Path);
        Assert.Equal("foreign bytes", File.ReadAllText(Path.Combine(external, "keep")));
        Assert.False(File.Exists(Path.Combine(external, "new-file")));
        // Remove only the disposable test link, never its target.
        Directory.Delete(link);
    }

    [Theory]
    [InlineData("url.https://foreign.invalid/.insteadof", "https://example.invalid/")]
    [InlineData("http.https://example.invalid/.sslverify", "false")]
    [InlineData("credential.helper", "!echo forbidden")]
    [InlineData("core.askpass", "echo forbidden")]
    [InlineData("remote.origin.uploadpack", "echo forbidden")]
    [InlineData("filter.hostile.smudge", "echo forbidden")]
    [InlineData("filter.LFS.smudge", "echo forbidden")]
    public async Task UnsafeTransportAndCheckoutConfigurationIsRefused(string key, string value)
    {
        using Fixture f = await Fixture.Create();
        await f.Run("config", key, value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Pool.OpenAsync(f.Repository, "feature", default));
        Assert.False(Directory.Exists(f.Repository.WorkspaceRoot));
    }

    [Fact]
    public async Task RealBranchFetchPinsOnlyItsDedicatedRefAndPreservesSourceAndFetchHead()
    {
        using Fixture f = await Fixture.Create();
        await f.Commit("feature", "file.txt", "remote target");
        string target = await f.Run("rev-parse", "feature");
        string remote = Path.Combine(f.Root, "remote.git");
        await Fixture.GitAt(f.Root, "clone", "--bare", f.Source, remote);
        await f.Run("switch", "main");
        string original = await f.Run("rev-parse", "HEAD");
        await f.Run("remote", "set-url", "origin", remote);
        await f.Run("config", "remote.origin.fetch", "+refs/heads/feature:refs/heads/main");
        await f.Run("update-ref", "refs/remotes/origin/feature", original);
        string fetchHead = Path.Combine(f.Source, ".git", "FETCH_HEAD"); File.WriteAllText(fetchHead, "user fetch evidence");
        File.WriteAllText(Path.Combine(f.Source, "file.txt"), "source user edit");
        Guid operation = Guid.NewGuid();
        Assert.Equal(target, await new GitCliWorkspaceGit().FetchBranchCommitAsync(f.Source, "feature", operation, default));
        Assert.Equal(original, await f.Run("rev-parse", "HEAD"));
        Assert.Equal(original, await f.Run("rev-parse", "refs/remotes/origin/feature"));
        Assert.Equal(target, await f.Run("rev-parse", "refs/git-workspace-pool/branches/" + operation.ToString("N")));
        Assert.Equal("source user edit", File.ReadAllText(Path.Combine(f.Source, "file.txt")));
        Assert.Equal("user fetch evidence", File.ReadAllText(fetchHead));
    }

    [Fact]
    public async Task BareSourceSupportsTheSamePrepareAndInPlaceAssignmentPath()
    {
        using Fixture f = await Fixture.Create();
        string bare = Path.Combine(f.Root, "bare.git");
        await Fixture.GitAt(f.Root, "clone", "--bare", f.Source, bare);
        await Fixture.GitAt(bare, "remote", "set-url", "origin", f.Repository.CloneUrl);
        GitRepositoryRegistration repository = f.Repository with { LocalPath = bare };
        WarmWorkspace donor = await f.Pool.PrepareAsync(repository, "main", default);
        Assert.Equal(donor.Path, (await f.Pool.OpenAsync(repository, "feature", default)).Path);
        Assert.Equal("true", await Fixture.GitAt(bare, "rev-parse", "--is-bare-repository"));
    }

    private sealed class Catalog(GitRepositoryRegistration repository) : IGitBranchCatalog
    {
        public GitRepositoryRegistration? Find(GitBranchRequest request) => GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl) ? repository : null;
    }
    private sealed class BrokenEditor : IEditorLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Editor unavailable");
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gwp-warm-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public string Database => Path.Combine(Root, "warm-v1", "workspaces.db");
        public string LockRoot => Path.Combine(Root, "locks");
        public GitRepositoryRegistration Repository => new("https://example.invalid/team/repo.git", Source, Path.Combine(Root, "pool"));
        public SqliteWarmWorkspaceRegistry Registry { get; private set; } = null!;
        public FixtureGit Git { get; private set; } = null!;
        public WarmWorkspacePool Pool { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            Fixture f = new(); Directory.CreateDirectory(f.Root);
            await GitAt(f.Root, "init", "--initial-branch=main", f.Source);
            await f.Run("config", "user.name", "Fixture"); await f.Run("config", "user.email", "fixture@example.invalid");
            await f.Run("remote", "add", "origin", f.Repository.CloneUrl);
            File.WriteAllText(Path.Combine(f.Source, ".gitignore"), "cache/\nretained.txt\n");
            File.WriteAllText(Path.Combine(f.Source, "file.txt"), "base");
            await f.Run("add", "."); await f.Run("commit", "-m", "base"); await f.Run("branch", "feature");
            f.Registry = new(f.Database); f.Git = new(); f.Pool = new(f.Registry, f.Git, new FileWorkspaceOperationLock(f.LockRoot));
            return f;
        }
        public Task<string> Run(params string[] args) => GitAt(Source, args);
        public async Task Commit(string branch, string relative, string content)
        {
            await Run("switch", branch); File.WriteAllText(Path.Combine(Source, relative), content);
            await Run("add", "--", relative); await Run("commit", "-m", "change " + relative);
        }
        public static async Task<string> GitAt(string path, params string[] args)
        {
            ProcessStartInfo start = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(path);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40)); await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            return (await output).Trim();
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal sealed class FixtureGit : IWarmWorktreeGit
    {
        private readonly WarmWorktreeGit _real = new();
        public int Fetches; public int Transitions; public bool FailCheckout;
        public Task VerifySourceAsync(GitRepositoryRegistration r, CancellationToken t) => _real.VerifySourceAsync(r, t);
        // Only transport is substituted: fixtures already contain every commit. Production never trusts file URLs.
        public Task<string> FetchAsync(GitRepositoryRegistration r, string branch, Guid operation, CancellationToken t)
        { Interlocked.Increment(ref Fetches); return Fixture.GitAt(r.LocalPath, "rev-parse", "refs/heads/" + branch); }
        public Task VerifyIdentityAsync(GitRepositoryRegistration r, WarmWorkspace w, CancellationToken t) => _real.VerifyIdentityAsync(r, w, t);
        public Task VerifyAvailableAsync(GitRepositoryRegistration r, WarmWorkspace w, string target, CancellationToken t) => _real.VerifyAvailableAsync(r, w, target, t);
        public Task<int> ChangedPathsAsync(string source, string donor, string target, CancellationToken t) => _real.ChangedPathsAsync(source, donor, target, t);
        public Task<string?> FingerprintAsync(GitRepositoryRegistration r, string commit, CancellationToken t) => _real.FingerprintAsync(r, commit, t);
        public Task TransitionAsync(string path, string target, CancellationToken t)
        { Interlocked.Increment(ref Transitions); if (FailCheckout) throw new InvalidOperationException("Injected failure after intent"); return _real.TransitionAsync(path, target, t); }
        public Task CreateAsync(GitRepositoryRegistration r, string path, string target, CancellationToken t) => _real.CreateAsync(r, path, target, t);
    }
}
