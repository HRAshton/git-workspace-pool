using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;
using GitWorkspacePool.Infrastructure.Transport;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Fast E2E coverage: real Git and filesystem, offline PR remote, fake editor.
/// </summary>
public sealed class GitWorkspaceE2ETests
{
    private const string PullRequestUrl = "https://github.com/owner/repo/pull/17";

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ProtocolUrlToRealGitWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string protocolUrl = "prworkspace://open?url="
            + Uri.EscapeDataString(PullRequestUrl)
            + "&v=1";

        Assert.True(OpenRequestProtocol.TryExtract(protocolUrl, out string requestUrl));
        OpenPullRequestResult result = await fixture.OpenAsync(requestUrl);

        Assert.True(await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath));
        Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_NamedPipeToRealGitAndFakeEditor()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<OpenPullRequestResult> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        string pipeName = "GitWorkspacePool.GitE2E." + Guid.NewGuid().ToString("N");
        string protocolUrl = "prworkspace://open?url="
            + Uri.EscapeDataString(PullRequestUrl)
            + "&v=1";

        NamedPipeRequestServer server = new(pipeName);
        Task listener = server.ListenAsync(
            message => _ = DispatchAsync(message),
            shutdown.Token
        );

        async Task DispatchAsync(string message)
        {
            try
            {
                if (!OpenRequestProtocol.TryExtract(message, out string requestUrl))
                {
                    throw new InvalidOperationException("Rejected protocol URL.");
                }

                completed.TrySetResult(await fixture.OpenAsync(requestUrl));
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
        }

        try
        {
            await new NamedPipeRequestClient().SendAsync(
                pipeName,
                protocolUrl,
                shutdown.Token
            );
            OpenPullRequestResult result = await completed.Task.WaitAsync(shutdown.Token);

            Assert.False(result.Reused);
            Assert.True(await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath));
            Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
        }
        finally
        {
            await shutdown.CancelAsync();
            await listener;
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_CreatesDetachedWorktreeFromLocalPullRequestRef()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult result = await fixture.OpenAsync(PullRequestUrl);

        Assert.False(result.Reused);
        Assert.Equal("feature for pr 17", File.ReadAllText(Path.Combine(result.WorkspacePath, "feature.txt")));
        Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
        Assert.True(
            await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath),
            "The path must be present in the source clone's Git worktree registry."
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ReopensWithoutOverwritingLocalChanges()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        string changedPath = Path.Combine(first.WorkspacePath, "feature.txt");
        string untrackedPath = Path.Combine(first.WorkspacePath, "notes.local");
        string binaryPath = Path.Combine(first.WorkspacePath, "image.local");
        byte[] binary = [0, 255, 19, 0, 42, 128];
        File.WriteAllText(changedPath, "user has unsaved work");
        File.WriteAllText(untrackedPath, "never delete me");
        File.WriteAllBytes(binaryPath, binary);

        OpenPullRequestResult second = await fixture.OpenAsync(PullRequestUrl);

        Assert.True(second.Reused);
        Assert.Equal(first.WorkspacePath, second.WorkspacePath);
        Assert.Equal("user has unsaved work", File.ReadAllText(changedPath));
        Assert.Equal("never delete me", File.ReadAllText(untrackedPath));
        Assert.Equal(binary, File.ReadAllBytes(binaryPath));
        Assert.Equal(2, fixture.Editor.OpenedPaths.Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_RefusesForeignDirectoryWithoutDeletingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string path = fixture.WorkspacePath(17);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "user.txt"), "keep");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "user.txt")));
        Assert.Empty(fixture.Editor.OpenedPaths);
        Assert.False(await fixture.IsRegisteredWorktreeAsync(path));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_QuarantinesUnjournalledGitWorktreeWithoutDataLoss()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.CreateUnjournalledWorktreeAsync();
        string workspacePath = fixture.WorkspacePath(17);
        string notePath = Path.Combine(workspacePath, "notes.local");
        File.WriteAllText(notePath, "old preview session");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Equal("old preview session", File.ReadAllText(notePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(workspacePath));
        Assert.Empty(fixture.Editor.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ConcurrentRequestsAllocateOneWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult[] results = await Task.WhenAll(
            fixture.OpenAsync(PullRequestUrl),
            fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Single(results.Where(result => !result.Reused));
        Assert.Single(results.Where(result => result.Reused));
        Assert.Equal(results[0].WorkspacePath, results[1].WorkspacePath);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(results[0].WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_RecoversInterruptedCheckoutAfterRestart()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SimulateInterruptedPreparationAsync(17);
        string workspacePath = fixture.WorkspacePath(17);
        File.WriteAllText(Path.Combine(workspacePath, "notes.local"), "saved user notes");

        OpenPullRequestResult result = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);

        Assert.True(result.Reused);
        Assert.Equal("saved user notes", File.ReadAllText(Path.Combine(workspacePath, "notes.local")));
        Assert.Equal(workspacePath, Assert.Single(fixture.Editor.OpenedPaths));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_DifferentHandlersCannotDoubleAllocate()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult[] results = await Task.WhenAll(
            fixture.OpenAsync(PullRequestUrl),
            fixture.OpenWithFreshHandlerAsync(PullRequestUrl)
        );

        Assert.Single(results.Where(result => !result.Reused));
        Assert.Single(results.Where(result => result.Reused));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(results[0].WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task RepositoryOrigin_RealGitReaderRejectsMismatchedLocalRemote()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        PullRequestReference request = new("github", "owner", "repo", 17);
        RepositoryRegistration registration = new(
            request.RepositoryKey,
            fixture.SourcePath,
            fixture.WorkspacePath(17)
        );

        GitHubOriginVerifier verifier = new(new GitCliWorkspaceGit());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyAsync(request, registration, CancellationToken.None)
        );
        Assert.False(Directory.Exists(fixture.WorkspacePath(17)));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_PreservesIndexStagedUnstagedBinaryIgnoredAndDeletions()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string worktree = opened.WorkspacePath;

        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "staged contents");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "unstaged contents");
        File.Delete(Path.Combine(worktree, "README.md"));
        File.WriteAllBytes(Path.Combine(worktree, "untracked.bin"), [0, 255, 19, 128]);
        File.WriteAllText(Path.Combine(worktree, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(worktree, "generated"));
        File.WriteAllBytes(Path.Combine(worktree, "generated", "ignored.bin"), [255, 0, 44]);

        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string manifestPath = Path.Combine(snapshot.Folder, "manifest.json");
        Assert.True(File.Exists(manifestPath));

        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;

        Assert.Equal(2, manifest.Version);
        Assert.Contains(manifest.StagedFiles, file => file.RelativePath == "feature.txt");
        Assert.Equal(
            "staged contents",
            File.ReadAllText(Path.Combine(snapshot.Folder, "staged", "feature.txt"))
        );
        Assert.Contains("README.md", manifest.MissingPaths);
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(snapshot.Folder, "files", "feature.txt")));
        Assert.Equal(
            new byte[] { 0, 255, 19, 128 },
            File.ReadAllBytes(Path.Combine(snapshot.Folder, "files", "untracked.bin"))
        );
        Assert.Equal(
            new byte[] { 255, 0, 44 },
            File.ReadAllBytes(Path.Combine(snapshot.Folder, "files", "generated", "ignored.bin"))
        );
        Assert.True(File.Exists(Path.Combine(snapshot.Folder, "index.bin")));
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(worktree, "feature.txt")));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(worktree));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);

        // Capturing the index must preserve the exact independently staged payload.
        string stagedBlob = await fixture.ReadStagedFeatureAsync();
        Assert.Equal("staged contents", stagedBlob);

        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);
        Assert.True(reopened.Reused);
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(worktree, "feature.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RestoreIntoNewWorktreePreservesIndexAndWorkingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = opened.WorkspacePath;
        File.WriteAllText(Path.Combine(source, "feature.txt"), "staged version");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(source, "feature.txt"), "working version");
        File.Delete(Path.Combine(source, "README.md"));
        File.WriteAllText(Path.Combine(source, ".gitignore"), "build/\n");
        Directory.CreateDirectory(Path.Combine(source, "build"));
        byte[] binary = [0, 255, 13, 128, 1];
        File.WriteAllBytes(Path.Combine(source, "build", "artifact.bin"), binary);
        File.WriteAllBytes(Path.Combine(source, "personal.bin"), binary);

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();

        // The source can continue evolving; restoration must use immutable saved data.
        File.WriteAllText(Path.Combine(source, "feature.txt"), "later unsaved work");

        string recovered = await fixture.RestoreSnapshotAsync(saved.SnapshotId);

        Assert.NotEqual(source, recovered);
        Assert.Equal("working version", File.ReadAllText(Path.Combine(recovered, "feature.txt")));
        Assert.Equal("staged version", await fixture.ReadStagedFeatureAsync(recovered));
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(recovered, "build", "artifact.bin")));
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(recovered, "personal.bin")));
        Assert.False(File.Exists(Path.Combine(recovered, "README.md")));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(recovered));
        Assert.Equal("later unsaved work", File.ReadAllText(Path.Combine(source, "feature.txt")));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_TamperedPayloadIsRejectedBeforeNewWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "original");

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        File.WriteAllText(Path.Combine(saved.Folder, "files", "feature.txt"), "tampered!");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(saved.SnapshotId)
        );

        Assert.Equal("original", File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt")));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_UnknownSnapshotCannotAllocateRecoveryWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(Guid.NewGuid())
        );
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RefusesLockedUserFileWithoutPublishing()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changedFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changedFile, "important work");

        using (FileStream held = new(
            changedFile,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        ))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.SnapshotAsync());
        }

        Assert.Equal("important work", File.ReadAllText(changedFile));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
        Assert.Empty(Directory.GetDirectories(fixture.SnapshotRoot, "snapshot-*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_UnknownPullRequestDoesNotAllocateWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync("https://github.com/owner/repo/pull/999")
        );

        Assert.False(Directory.Exists(fixture.WorkspacePath(999)));
        Assert.Empty(fixture.Editor.OpenedPaths);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _source;
        private readonly string _workspaces;
        private readonly OpenPullRequestHandler _handler;

        private Fixture(string root, string source, string workspaces, FakeEditor editor)
        {
            _root = root;
            _source = source;
            _workspaces = workspaces;
            Editor = editor;

            _handler = CreateHandler();

        }

        public string SnapshotRoot => Path.Combine(_root, "snapshots");

        public string RecoveryRoot => Path.Combine(_root, "recovered");

        public string SourcePath => _source;

        public FakeEditor Editor { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "git-workspace-pool-e2e-" + Guid.NewGuid().ToString("N")
            );
            string remote = Path.Combine(root, "remote.git");
            string seed = Path.Combine(root, "seed");
            string source = Path.Combine(root, "source");
            string workspaces = Path.Combine(root, "workspaces");

            Directory.CreateDirectory(root);

            try
            {
                await GitAsync(root, "init", "--bare", "--initial-branch=main", remote);
                await GitAsync(root, "init", "--initial-branch=main", seed);
                await GitAsync(seed, "config", "user.name", "E2E Fixture");
                await GitAsync(seed, "config", "user.email", "test@example.invalid");

                File.WriteAllText(Path.Combine(seed, "README.md"), "base");
                await GitAsync(seed, "add", "README.md");
                await GitAsync(seed, "commit", "-m", "base");
                await GitAsync(seed, "remote", "add", "origin", remote);
                await GitAsync(seed, "push", "origin", "HEAD:refs/heads/main");

                await GitAsync(seed, "checkout", "-b", "feature/pr-17");
                File.WriteAllText(Path.Combine(seed, "feature.txt"), "feature for pr 17");
                await GitAsync(seed, "add", "feature.txt");
                await GitAsync(seed, "commit", "-m", "PR 17");
                await GitAsync(seed, "push", "origin", "HEAD:refs/pull/17/head");
                await GitAsync(root, "clone", "--no-local", remote, source);

                return new Fixture(root, source, workspaces, new FakeEditor());
            }
            catch
            {
                DeleteFixtureTree(root);
                throw;
            }
        }

        public async Task CreateUnjournalledWorktreeAsync()
        {
            await GitAsync(_source, "fetch", "--no-tags", "origin", "refs/pull/17/head");
            Directory.CreateDirectory(_workspaces);
            await GitAsync(
                _source,
                "worktree",
                "add",
                "--detach",
                WorkspacePath(17),
                "FETCH_HEAD"
            );
        }

        public Task GitInWorkspaceAsync(params string[] arguments) =>
            GitAsync(WorkspacePath(17), arguments);

        public async Task<string> ReadStagedFeatureAsync(string? worktreePath = null)
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = worktreePath ?? WorkspacePath(17),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add(":feature.txt");
            using Process process = Process.Start(startInfo)!;
            string result = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return result.TrimEnd('\r', '\n');
        }

        public async Task<WorkspaceSession> OwnedSessionAsync()
        {
            SqliteWorkspaceSessionJournal journal = new(
                Path.Combine(_root, "state", "sessions.db")
            );
            WorkspaceSession? session = Assert.Single(
                await journal.ListActiveAsync(CancellationToken.None)
            );
            return session;
        }

        public async Task<WorkspaceSnapshotReceipt> SnapshotAsync()
        {
            WorkspaceSession session = await OwnedSessionAsync();
            string state = Path.Combine(_root, "state");
            CaptureWorkspaceSnapshotService handler = new(
                new SqliteWorkspaceSessionJournal(Path.Combine(state, "sessions.db")),
                new FileWorkspaceOperationLock(Path.Combine(state, "locks")),
                new GitCliWorkspaceGit(),
                new VerifiedWorkspaceSnapshotExporter(
                    SnapshotRoot,
                    new GitWorktreeSnapshotScanner()
                )
            );
            return await handler.CaptureAsync(session.OperationId, CancellationToken.None);
        }

        public async Task<string> RestoreSnapshotAsync(Guid snapshotId)
        {
            SqliteWorkspaceSessionJournal sessions = new(
                Path.Combine(_root, "state", "sessions.db")
            );
            RestoreWorkspaceSnapshotService service = new(
                sessions,
                new VerifiedWorkspaceSnapshotRestorer(
                    RecoveryRoot,
                    new GitCliWorkspaceGit(),
                    new GitWorktreeSnapshotScanner()
                )
            );
            return await service.RestoreAsync(snapshotId, CancellationToken.None);
        }

        public Task<OpenPullRequestResult> OpenAsync(string url) =>
            _handler.HandleAsync(url, CancellationToken.None);

        public Task<OpenPullRequestResult> OpenWithFreshHandlerAsync(string url) =>
            CreateHandler().HandleAsync(url, CancellationToken.None);

        public async Task SimulateInterruptedPreparationAsync(int pullRequestNumber)
        {
            string statePath = Path.Combine(_root, "state", "sessions.db");
            SqliteWorkspaceSessionJournal journal = new(statePath);
            WorkspaceSession session = await journal.GetOrCreateAsync(
                "github:owner/repo",
                pullRequestNumber,
                _source,
                WorkspacePath(pullRequestNumber),
                CancellationToken.None
            );
            GitCliWorkspaceGit git = new();
            await git.FetchPullRequestAsync(_source, pullRequestNumber, CancellationToken.None);
            string pinned = await git.GetPullRequestCommitAsync(
                _source,
                pullRequestNumber,
                CancellationToken.None
            );
            await journal.PinCommitAsync(session.OperationId, pinned, CancellationToken.None);
            Directory.CreateDirectory(_workspaces);
            await git.CreateWorktreeAsync(
                _source,
                WorkspacePath(pullRequestNumber),
                pinned,
                CancellationToken.None
            );

            // Simulate process termination before the journal's Activated transition.
        }

        private OpenPullRequestHandler CreateHandler()
        {
            RepositoryRegistration registration = new("owner/repo", _source, _workspaces);
            string stateDirectory = Path.Combine(_root, "state");
            WorkspaceAcquisitionService acquisition = new(
                new GitCliWorkspaceGit(),
                new WorkspaceDirectories(),
                new SqliteWorkspaceSessionJournal(Path.Combine(stateDirectory, "sessions.db")),
                new FileWorkspaceOperationLock(Path.Combine(stateDirectory, "locks"))
            );
            return new OpenPullRequestHandler(
                new Catalog(registration),
                new LocalFixtureOriginVerifier(),
                acquisition,
                Editor
            );
        }

        public string WorkspacePath(int number) => Path.Combine(
            _workspaces,
            $"github-owner-repo-pr-{number}"
        );

        public Task<bool> IsRegisteredWorktreeAsync(string path) =>
            new GitCliWorkspaceGit().IsRegisteredWorktreeAsync(
                _source,
                path,
                CancellationToken.None
            );

        public void Dispose()
        {
            DeleteFixtureTree(_root);
        }

        private static void DeleteFixtureTree(string root)
        {
            // Git object files are read-only on Windows. This is disposable test data only.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }

        private static async Task GitAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Git is not installed.");

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            string stderr = await error;
            _ = await output;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git {string.Join(' ', arguments)} failed: {stderr}"
                );
            }
        }
    }

    private sealed class LocalFixtureOriginVerifier : IRepositoryOriginVerifier
    {
        public Task VerifyAsync(
            PullRequestReference request,
            RepositoryRegistration registration,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class Catalog(RepositoryRegistration registration) : IRepositoryCatalog
    {
        public RepositoryRegistration? Find(PullRequestReference request) =>
            request.RepositoryKey.Equals(registration.RepositoryKey, StringComparison.OrdinalIgnoreCase)
                ? registration
                : null;
    }

    private sealed class FakeEditor : IEditorLauncher
    {
        private readonly ConcurrentQueue<string> _openedPaths = new();

        public IReadOnlyCollection<string> OpenedPaths => _openedPaths.ToArray();

        public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
        {
            _openedPaths.Enqueue(workspacePath);
            return Task.CompletedTask;
        }
    }
}
