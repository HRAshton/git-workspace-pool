using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

public sealed class GitBranchWorkspaceE2ETests
{
    private const string CloneUrl = "https://bitbucket.example.com/stash/scm/team/repo.git";
    private static readonly GitBranchRequest Request = new(CloneUrl, "feature/work");

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ProtocolToRealGitPinsCommitAndReopenPreservesEveryKindOfLocalEdit()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Assert.True(GitBranchProtocol.TryParse(GitBranchProtocolTests.Link(CloneUrl, Request.Branch), out GitBranchRequest? request));
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(request, CancellationToken.None);
        Assert.False(first.Reused);
        Assert.Equal(fixture.Commit, await Fixture.GitAsync(first.WorkspacePath, "rev-parse", "HEAD"));
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "staged");
        await Fixture.GitAsync(first.WorkspacePath, "add", "tracked.txt");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "unstaged");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "untracked.txt"), "keep untracked");
        File.WriteAllBytes(Path.Combine(first.WorkspacePath, "ignored.bin"), [0, 1, 255]);
        string before = await Fixture.GitAsync(first.WorkspacePath, "status", "--porcelain", "--ignored");
        // A new handler and store simulate tray restart. A nonexistent remote must not be fetched on reopen.
        await Fixture.GitAsync(fixture.Source, "remote", "set-url", "origin", Path.Combine(fixture.Root, "missing.git"));
        OpenPullRequestResult reopened = await fixture.Handler().HandleAsync(request, CancellationToken.None);
        Assert.True(reopened.Reused);
        Assert.Equal(first.WorkspacePath, reopened.WorkspacePath);
        Assert.Equal(before, await Fixture.GitAsync(first.WorkspacePath, "status", "--porcelain", "--ignored"));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(first.WorkspacePath, "tracked.txt")));
        Assert.Equal("staged", await Fixture.GitAsync(first.WorkspacePath, "show", ":tracked.txt"));
        Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(Path.Combine(first.WorkspacePath, "ignored.bin")));
        Assert.Equal(2, fixture.Editor.Opened.Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task MissingBranchRetainsPreparingAndCanRetryWithoutDeletingAnything()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchRequest missing = new(CloneUrl, "missing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(missing, CancellationToken.None));
        GitBranchSession pending = (await fixture.Store().FindAsync(missing, CancellationToken.None))!;
        Assert.Equal(WorkspaceSessionState.Preparing, pending.State);
        Assert.Null(pending.PinnedCommit);
        Assert.False(Directory.Exists(pending.WorkspacePath));
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/missing");
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(missing, CancellationToken.None);
        Assert.False(opened.Reused);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(missing, CancellationToken.None))!.State);
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCheckoutOnlyReconcilesCleanPinnedOwnedWorktree(bool dirty)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await fixture.Store().PinAsync(session.OperationId, fixture.Commit, CancellationToken.None);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, session.WorkspacePath, fixture.Commit, CancellationToken.None);
        if (dirty) File.WriteAllText(Path.Combine(session.WorkspacePath, "tracked.txt"), "user work after interruption");
        if (dirty)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
            Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
            Assert.Equal("user work after interruption", File.ReadAllText(Path.Combine(session.WorkspacePath, "tracked.txt")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
            Assert.Empty(fixture.Editor.Opened);
        }
        else
        {
            Assert.True((await fixture.Handler().HandleAsync(Request, CancellationToken.None)).Reused);
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task PinnedBeforeCrashCreatesOriginalCommitEvenWhenRemoteBranchMoves()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await fixture.Store().PinAsync(session.OperationId, fixture.Commit, CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "new remote head");
        await Fixture.GitAsync(fixture.Source, "add", "tracked.txt");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "advance branch");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work");
        OpenPullRequestResult result = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        Assert.Equal(fixture.Commit, await Fixture.GitAsync(result.WorkspacePath, "rev-parse", "HEAD"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(result.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task UnknownOccupiedPathIsNeverAdoptedOrDeleted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string path = Path.Combine(fixture.Repository.WorkspaceRoot, Request.WorkspaceName);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "foreign.txt"), "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.Null(await fixture.Store().FindAsync(Request, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "foreign.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task WrongRealOriginRejectedBeforeReservationOrWorkspaceCreation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler(new GitCliWorkspaceGit()).HandleAsync(Request, CancellationToken.None));
        Assert.Null(await fixture.Store().FindAsync(Request, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Repository.WorkspaceRoot));
        Assert.Empty(fixture.Editor.Opened);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ConcurrentClicksShareOneDurableOwnerAndOneCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => fixture.Handler().HandleAsync(Request, CancellationToken.None))));
        Assert.Single(results.Where(r => !r.Reused));
        Assert.Single(results.Select(r => r.WorkspacePath).Distinct());
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData("credential.helper", "!echo unsafe")]
    [InlineData("core.sshCommand", "unsafe")]
    [InlineData("url.https://elsewhere.invalid/.insteadOf", "https://bitbucket.example.com/")]
    public async Task BranchFetchRejectsUnsafeGitConfiguration(string key, string value)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "config", "--local", key, value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitCliWorkspaceGit().FetchBranchCommitAsync(
            fixture.Source, Request.Branch, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task StoreUsesCaseSensitiveBranchesAndRejectsOwnershipAndPinReplacement()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession first = await fixture.ReserveAsync(Request);
        GitBranchSession other = await fixture.ReserveAsync(new(CloneUrl, "feature/Work"));
        Assert.NotEqual(first.OperationId, other.OperationId);
        Assert.NotEqual(first.WorkspacePath, other.WorkspacePath);
        await fixture.Store().PinAsync(first.OperationId, fixture.Commit, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().PinAsync(first.OperationId, new string('a', 40), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().ReserveAsync(Request,
            fixture.Repository with { LocalPath = Path.Combine(fixture.Root, "different") }, first.WorkspacePath, CancellationToken.None));
        Assert.Equal(2, (await fixture.Store().ListUnfinishedAsync(CancellationToken.None)).Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task MissingActiveWorktreeIsQuarantinedInsteadOfRecreated()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        string retained = first.WorkspacePath + "-retained";
        Directory.Move(first.WorkspacePath, retained);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.False(Directory.Exists(first.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(retained, "tracked.txt")));
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task UnpinnedExistingCheckoutIsQuarantinedInsteadOfAdopted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, session.WorkspacePath, fixture.Commit, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        Assert.True(File.Exists(Path.Combine(session.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task DifferentBranchesOpenIndependentlyWithNamedCredentialHelper()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "config", "credential.helper", "manager");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        OpenPullRequestResult[] opened = await Task.WhenAll(
            Task.Run(() => fixture.Handler().HandleAsync(Request, CancellationToken.None)),
            Task.Run(() => fixture.Handler().HandleAsync(new(CloneUrl, "other"), CancellationToken.None)));
        Assert.NotEqual(opened[0].WorkspacePath, opened[1].WorkspacePath);
        Assert.All(opened, result => Assert.False(result.Reused));
        foreach (OpenPullRequestResult result in opened)
            Assert.Equal(fixture.Commit, await Fixture.GitAsync(result.WorkspacePath, "rev-parse", "HEAD"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gwp-branch-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public string Database => Path.Combine(Root, "state", "sessions.db");
        public string Commit { get; private set; } = "";
        public Editor Editor { get; } = new();
        public GitRepositoryRegistration Repository => new(CloneUrl, Source, Path.Combine(Root, "workspaces"));

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.Root);
            string remote = Path.Combine(fixture.Root, "remote.git");
            await GitAsync(fixture.Root, "init", "--bare", "--initial-branch=main", remote);
            await GitAsync(fixture.Root, "clone", remote, fixture.Source);
            await GitAsync(fixture.Source, "config", "user.name", "Fixture");
            await GitAsync(fixture.Source, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "original");
            File.WriteAllText(Path.Combine(fixture.Source, ".gitignore"), "ignored.bin\n");
            await GitAsync(fixture.Source, "add", ".");
            await GitAsync(fixture.Source, "commit", "-m", "initial");
            await GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work");
            fixture.Commit = await GitAsync(fixture.Source, "rev-parse", "HEAD");
            // Initialize the existing PR schema too: branch ownership must coexist with it.
            _ = new SqliteWorkspaceSessionJournal(fixture.Database);
            _ = fixture.Store();
            return fixture;
        }

        public SqliteGitBranchSessionStore Store() => new(Database);

        public Task<GitBranchSession> ReserveAsync(GitBranchRequest request) =>
            Store().ReserveAsync(request, Repository, Path.Combine(Repository.WorkspaceRoot, request.WorkspaceName), CancellationToken.None);

        public OpenGitBranchHandler Handler(IGitRemoteReader? remotes = null)
        {
            GitCliWorkspaceGit git = new();
            // Offline fixture replaces only remote identity, never Git fetch/checkout, SQLite or locks.
            return new(new Catalog(Repository), remotes ?? new RemoteReader(), git, git, Store(),
                new FileWorkspaceOperationLock(Path.Combine(Root, "locks")), new WorkspaceDirectories(),
                new WorkspaceReadySlotSafety(), new VerifiedReadySlotCheckout(), Editor);
        }

        public void Dispose()
        {
            // Fixture directories are disposable; production code never removes managed worktrees.
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }

        public static async Task<string> GitAsync(string directory, params string[] args)
        {
            ProcessStartInfo start = new("git")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.TrimEnd('\r', '\n');
        }
    }

    private sealed class Catalog(GitRepositoryRegistration repository) : IGitBranchCatalog
    {
        public GitRepositoryRegistration? Find(GitBranchRequest request) => repository;
    }

    private sealed class RemoteReader : IGitRemoteReader
    {
        public Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken) => Task.FromResult(CloneUrl);
    }

    private sealed class Editor : IEditorLauncher
    {
        public List<string> Opened { get; } = [];
        public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
        {
            lock (Opened) Opened.Add(workspacePath);
            return Task.CompletedTask;
        }
    }
}
