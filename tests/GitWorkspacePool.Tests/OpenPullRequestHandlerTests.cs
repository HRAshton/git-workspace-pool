using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class OpenPullRequestHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReopensExistingWorktreeWithoutFetchingOrCheckout()
    {
        Fixture fixture = new() { AlreadyExists = true };
        fixture.Git.Registered = true;

        OpenPullRequestResult result = await fixture.Handler.HandleAsync(
            "https://github.com/owner/repo/pull/17",
            CancellationToken.None
        );

        Assert.True(result.Reused);
        Assert.Equal(0, fixture.Git.Fetches);
        Assert.Equal(0, fixture.Git.Creates);
        Assert.Equal(result.WorkspacePath, fixture.Editor.OpenedPath);
    }

    [Fact]
    public async Task HandleAsync_NewWorktreeFetchesAndCreatesExactlyOnce()
    {
        Fixture fixture = new();

        OpenPullRequestResult result = await fixture.Handler.HandleAsync(
            "https://github.com/owner/repo/pull/17",
            CancellationToken.None
        );

        Assert.False(result.Reused);
        Assert.Equal(1, fixture.Git.Fetches);
        Assert.Equal(1, fixture.Git.Creates);
        Assert.Equal(17, fixture.Git.LastFetchedPull);
        Assert.Equal(result.WorkspacePath, fixture.Editor.OpenedPath);
    }

    [Fact]
    public async Task HandleAsync_RefusesForeignFolderWithoutModifyingIt()
    {
        Fixture fixture = new() { AlreadyExists = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                fixture.Handler.HandleAsync(
                    "https://github.com/owner/repo/pull/17",
                    CancellationToken.None
                )
        );

        Assert.Equal(0, fixture.Git.Fetches);
        Assert.Equal(0, fixture.Git.Creates);
        Assert.Null(fixture.Editor.OpenedPath);
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

    private sealed class Fixture
    {
        public readonly FakeGit Git = new();
        public readonly FakeEditor Editor = new();

        public Fixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "gwp-unit-test");
            RepositoryRegistration registration = new(
                "owner/repo",
                Path.Combine(root, "source"),
                Path.Combine(root, "workspaces")
            );
            Handler = new OpenPullRequestHandler(
                new FakeCatalog(registration),
                Git,
                new FakeDirectories(this),
                Editor
            );
        }

        public bool AlreadyExists { get; init; }

        public OpenPullRequestHandler Handler { get; }

        private sealed class FakeDirectories(Fixture fixture) : IWorkspaceDirectories
        {
            public bool Exists(string path) => fixture.AlreadyExists;

            public void EnsureRoot(string root) { }
        }
    }

    private sealed class FakeCatalog(RepositoryRegistration registration) : IRepositoryCatalog
    {
        public RepositoryRegistration? Find(PullRequestReference request) =>
            request.RepositoryKey.Equals(
                registration.RepositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
                ? registration
                : null;
    }

    private sealed class FakeGit : IWorkspaceGit
    {
        public int Fetches { get; private set; }

        public int Creates { get; private set; }

        public int LastFetchedPull { get; private set; }

        public Task<bool> IsRegisteredWorktreeAsync(
            string repositoryPath,
            string workspacePath,
            CancellationToken cancellationToken
        ) => Task.FromResult(Registered);

        public bool Registered { get; set; }

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

        public Task CreateWorktreeAsync(
            string repositoryPath,
            string workspacePath,
            CancellationToken cancellationToken
        )
        {
            Creates++;
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
