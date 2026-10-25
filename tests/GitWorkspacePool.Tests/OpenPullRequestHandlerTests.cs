using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class OpenPullRequestHandlerTests
{
    private const string Request = "https://github.com/owner/repo/pull/17";
    private static readonly string CommitSha = new('a', 40);

    [Fact]
    public async Task HandleAsync_ReopensOwnedWorktreeWithoutFetchingOrCheckout()
    {
        Fixture fixture = new() { AlreadyExists = true };
        fixture.Git.Registered = true;
        await fixture.SeedActiveAsync();

        OpenWorkspaceResult result = await fixture.Handler.HandleAsync(
            Request,
            CancellationToken.None
        );

        Assert.True(result.Reused);
        Assert.Equal(0, fixture.Git.Fetches);
        Assert.Equal(0, fixture.Git.Creates);
        Assert.Equal(result.WorkspacePath, fixture.Editor.OpenedPath);
    }

    [Fact]
    public async Task HandleAsync_NewWorktreePinsAndCreatesExactlyOnce()
    {
        Fixture fixture = new();

        OpenWorkspaceResult result = await fixture.Handler.HandleAsync(
            Request,
            CancellationToken.None
        );

        Assert.False(result.Reused);
        Assert.Equal(1, fixture.Git.Fetches);
        Assert.Equal(1, fixture.Git.Creates);
        Assert.Equal(17, fixture.Git.LastFetchedPull);
        Assert.Equal(CommitSha, fixture.Git.CreatedCommit);
        Assert.Equal(result.WorkspacePath, fixture.Editor.OpenedPath);

        WorkspaceSession session = await fixture.LoadSessionAsync();
        Assert.Equal(WorkspaceSessionState.Active, session.State);
        Assert.Equal(CommitSha, session.PinnedCommitSha);
    }

    [Fact]
    public async Task HandleAsync_RefusesForeignFolderWithoutModifyingIt()
    {
        Fixture fixture = new() { AlreadyExists = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(Request, CancellationToken.None)
        );

        Assert.Equal(0, fixture.Git.Fetches);
        Assert.Equal(0, fixture.Git.Creates);
        Assert.Null(fixture.Editor.OpenedPath);
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.LoadSessionAsync()).State);
    }

    [Fact]
    public async Task HandleAsync_RefusesUnconfiguredRepository()
    {
        Fixture fixture = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                fixture.Handler.HandleAsync(
                    "https://github.com/other/repo/pull/17",
                    CancellationToken.None
                )
        );

        Assert.Equal(0, fixture.Git.Fetches);
        Assert.Equal(0, fixture.Git.Creates);
    }

    [Fact]
    public async Task HandleAsync_RecoversAfterCrashBetweenCheckoutAndJournalCommit()
    {
        Fixture fixture = new();
        fixture.Git.FailAfterCreation = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(Request, CancellationToken.None)
        );

        Assert.Equal(WorkspaceSessionState.Preparing, (await fixture.LoadSessionAsync()).State);
        fixture.Git.FailAfterCreation = false;

        OpenWorkspaceResult result = await fixture.Handler.HandleAsync(
            Request,
            CancellationToken.None
        );

        Assert.True(result.Reused);
        Assert.Equal(1, fixture.Git.Fetches);
        Assert.Equal(1, fixture.Git.Creates);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.LoadSessionAsync()).State);
    }

    [Fact]
    public async Task HandleAsync_DoesNotOverwriteMismatchedRecoveredWorktree()
    {
        Fixture fixture = new();
        fixture.Git.FailAfterCreation = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(Request, CancellationToken.None)
        );

        fixture.Git.HeadCommit = new string('b', 40);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(Request, CancellationToken.None)
        );

        Assert.Null(fixture.Editor.OpenedPath);
        Assert.Equal(1, fixture.Git.Creates);
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.LoadSessionAsync()).State);
    }

    [Fact]
    public async Task HandleAsync_QuarantinesMissingActiveWorkspace()
    {
        Fixture fixture = new();
        await fixture.SeedActiveAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(Request, CancellationToken.None)
        );

        Assert.Equal(0, fixture.Git.Creates);
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.LoadSessionAsync()).State);
    }

    private sealed class Fixture
    {
        private readonly RepositoryRegistration _registration;
        private readonly FakeJournal _journal = new();

        public Fixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "gwp-unit-test");
            _registration = new RepositoryRegistration(
                "owner/repo",
                Path.Combine(root, "source"),
                Path.Combine(root, "workspaces")
            );

            WorkspaceAcquisitionService acquisition = new(
                Git,
                new FakeDirectories(this),
                _journal,
                new FakeLock()
            );
            Handler = new OpenPullRequestHandler(
                new FakeCatalog(_registration),
                new FakeOriginVerifier(),
                acquisition,
                Editor
            );
        }

        public FakeGit Git { get; } = new();

        public FakeEditor Editor { get; } = new();

        public bool AlreadyExists { get; init; }

        public OpenPullRequestHandler Handler { get; }

        public Task<WorkspaceSession> LoadSessionAsync() => _journal.GetOrCreateAsync(
            "github:owner/repo",
            17,
            _registration.LocalPath,
            Path.Combine(_registration.WorkspaceRoot, "github-owner-repo-pr-17"),
            CancellationToken.None
        );

        public async Task SeedActiveAsync()
        {
            WorkspaceSession session = await LoadSessionAsync();
            await _journal.PinCommitAsync(session.OperationId, CommitSha, CancellationToken.None);
            await _journal.ActivateAsync(session.OperationId, CancellationToken.None);
        }

        private sealed class FakeDirectories(Fixture fixture) : IWorkspaceDirectories
        {
            public bool Exists(string path) => fixture.AlreadyExists || fixture.Git.Created;

            public void EnsureRoot(string root) { }
        }
    }

    private sealed class FakeCatalog(RepositoryRegistration registration) : IRepositoryCatalog
    {
        public RepositoryRegistration? Find(PullRequestReference request)
        {
            return request.RepositoryKey.Equals(
                registration.RepositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
                ? registration
                : null;
        }
    }

    private sealed class FakeJournal : IWorkspaceSessionJournal
    {
        private WorkspaceSession? _session;

        public Task<WorkspaceSession> GetOrCreateAsync(
            string repositoryKey,
            int pullRequestNumber,
            string sourcePath,
            string workspacePath,
            CancellationToken cancellationToken
        )
        {
            _session ??= new WorkspaceSession(
                Guid.NewGuid(),
                repositoryKey,
                pullRequestNumber,
                sourcePath,
                workspacePath,
                WorkspaceSessionState.Preparing,
                null
            );
            return Task.FromResult(_session);
        }

        public Task<WorkspaceSession?> FindByPullRequestAsync(
            string repositoryKey,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) => Task.FromResult(
            _session is not null
                && _session.RepositoryKey.Equals(repositoryKey, StringComparison.OrdinalIgnoreCase)
                && _session.PullRequestNumber == pullRequestNumber
                ? _session : null
        );

        public Task PinCommitAsync(
            Guid operationId,
            string commitSha,
            CancellationToken cancellationToken
        )
        {
            _session = _session! with { PinnedCommitSha = commitSha };
            return Task.CompletedTask;
        }

        public Task ActivateAsync(Guid operationId, CancellationToken cancellationToken)
        {
            _session = _session! with { State = WorkspaceSessionState.Active };
            return Task.CompletedTask;
        }

        public Task SuspendAsync(Guid operationId, CancellationToken cancellationToken)
        {
            _session = _session! with { State = WorkspaceSessionState.Suspended };
            return Task.CompletedTask;
        }

        public Task ResumeAsync(Guid operationId, CancellationToken cancellationToken)
        {
            _session = _session! with { State = WorkspaceSessionState.Active };
            return Task.CompletedTask;
        }

        public Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken)
        {
            _session = _session! with { State = WorkspaceSessionState.RecoveryRequired };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOriginVerifier : IRepositoryOriginVerifier
    {
        public Task VerifyAsync(
            PullRequestReference request,
            RepositoryRegistration registration,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class FakeLock : IWorkspaceOperationLock
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<IAsyncDisposable> AcquireAsync(
            string workspacePath,
            CancellationToken cancellationToken
        )
        {
            await _gate.WaitAsync(cancellationToken);
            return new Releaser(_gate);
        }

        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeGit : IWorkspaceGit, ILegacyWorkspaceGit
    {
        public int Fetches { get; private set; }

        public int Creates { get; private set; }

        public int LastFetchedPull { get; private set; }

        public string? CreatedCommit { get; private set; }

        public bool Registered { get; set; }

        public bool Created { get; private set; }

        public bool FailAfterCreation { get; set; }

        public string HeadCommit { get; set; } = CommitSha;

        public Task<bool> IsRegisteredWorktreeAsync(
            string repositoryPath,
            string workspacePath,
            CancellationToken cancellationToken
        ) => Task.FromResult(Registered || Created);

        public Task<string> GetOriginUrlAsync(
            string repositoryPath,
            CancellationToken cancellationToken
        ) => Task.FromResult("https://github.com/owner/repo.git");

        public Task FetchPullRequestAsync(
            string repositoryPath,
            int pullRequestNumber,
            CancellationToken cancellationToken
        )
        {
            Fetches++;
            LastFetchedPull = pullRequestNumber;
            return Task.CompletedTask;
        }

        public Task<string> GetPullRequestCommitAsync(
            string repositoryPath,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) => Task.FromResult(CommitSha);

        public Task<string> GetWorktreeCommitAsync(
            string workspacePath,
            CancellationToken cancellationToken
        ) => Task.FromResult(HeadCommit);

        public Task CreateWorktreeAsync(
            string repositoryPath,
            string workspacePath,
            string commitSha,
            CancellationToken cancellationToken
        )
        {
            Creates++;
            CreatedCommit = commitSha;
            Created = true;

            if (FailAfterCreation)
            {
                throw new InvalidOperationException("Simulated crash after checkout.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeEditor : IEditorLauncher
    {
        public string? OpenedPath { get; private set; }

        public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
        {
            OpenedPath = workspacePath;
            return Task.CompletedTask;
        }
    }
}
