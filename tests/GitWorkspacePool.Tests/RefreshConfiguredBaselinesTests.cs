using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class RefreshConfiguredBaselinesTests
{
    private const string Url = "https://bitbucket.example.com/scm/team/repo.git";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshIsExplicitSequentialBoundedAndObservesTheInterval()
    {
        Fetcher fetch = new();
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "workspaces"))
        { BaselineBranches = Enumerable.Range(0, 7).Select(i => "release/" + i).ToArray() };
        BaselineRefreshBatch first = await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        Assert.Equal(4, first.Attempts.Count);
        Assert.True(first.MoreDue);
        Assert.All(first.Attempts, status => Assert.True(status.Succeeded));
        BaselineRefreshBatch second = await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        Assert.Equal(3, second.Attempts.Count);
        Assert.False(second.MoreDue);
        Assert.Equal(7, fetch.Branches.Count);
        Assert.Empty((await service.RefreshDueAsync([repository], 30, Now.AddMinutes(1), false, CancellationToken.None)).Attempts);
        Assert.Equal(4, (await service.RefreshDueAsync([repository], 30, Now.AddMinutes(31), false, CancellationToken.None)).Attempts.Count);
        Assert.Equal(7, service.Latest().Count);
        Assert.All(service.Latest(), status => Assert.False(status.InProgress));
    }

    [Fact]
    public async Task WrongOriginNeverFetchesAndFailuresDoNotCreateRetryStorms()
    {
        Fetcher fetch = new();
        RefreshConfiguredBaselinesService service = new(new Remote("https://elsewhere.invalid/repo.git"), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root")) { BaselineBranches = ["main"] };
        BaselineRefreshStatus status = Assert.Single((await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None)).Attempts);
        Assert.False(status.Succeeded);
        Assert.False(status.InProgress);
        Assert.Empty(fetch.Branches);
        Assert.Empty((await service.RefreshDueAsync([repository], 30, Now.AddMinutes(1), false, CancellationToken.None)).Attempts);
    }

    [Fact]
    public async Task EmptyConfigurationDoesNotFetchAndRemovedRefsDoNotAccumulateState()
    {
        Fetcher fetch = new();
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        Assert.Empty((await service.RefreshDueAsync([], 30, Now, false, CancellationToken.None)).Attempts);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root")) { BaselineBranches = ["main"] };
        await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        Assert.Single(service.Latest());
        await service.RefreshDueAsync([], 30, Now, false, CancellationToken.None);
        Assert.Empty(service.Latest());
        Assert.Single(fetch.Branches);
    }

    [Theory]
    [InlineData("release/*")]
    [InlineData("../main")]
    [InlineData("feature:refs/heads/main")]
    public async Task InvalidBaselineConfigurationIsRejectedBeforeFetch(string branch)
    {
        Fetcher fetch = new();
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root")) { BaselineBranches = [branch] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None));
        Assert.Empty(fetch.Branches);
    }

    [Fact]
    public async Task ConcurrentRefreshDoesNotDuplicateFetchAndStatusDistinguishesRunningFromFailed()
    {
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Fetcher fetch = new()
        {
            Before = async token => { started.TrySetResult(true); await finish.Task.WaitAsync(token); },
        };
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root"))
        { BaselineBranches = ["main"] };
        Task<BaselineRefreshBatch> first = service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Assert.Single(service.Latest()).InProgress);
            Assert.True((await service.RefreshDueAsync([repository], 30, Now, true, CancellationToken.None)).AlreadyRunning);
            Assert.Single(fetch.Branches);
        }
        finally { finish.TrySetResult(true); }
        Assert.True(Assert.Single((await first).Attempts).Succeeded);
        Assert.False(Assert.Single(service.Latest()).InProgress);
    }

    private sealed class Remote(string url = Url) : IGitRemoteReader
    {
        public Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken) => Task.FromResult(url);
    }

    private sealed class Fetcher : IGitBaselineFetcher
    {
        public List<string> Branches { get; } = [];
        public Func<CancellationToken, Task>? Before { get; init; }
        public async Task<string> FetchBaselineCommitAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
        {
            Branches.Add(branch);
            if (Before is not null) await Before(cancellationToken);
            return new string('a', 40);
        }
    }
}
