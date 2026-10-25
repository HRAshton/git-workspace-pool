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

    [Fact]
    public async Task RegexDiscoveryFetchesOnlyMatchingAdvertisedHeadsAndCachesTheInventory()
    {
        Fetcher fetch = new();
        fetch.Advertised.AddRange(["master", "2026r150", "2026R151", "feature/2026r150", "2026r15", "main"]);
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root"))
        { BaselinePatterns = [@"^20\d\d[rR]\d{3}$"] };
        BaselineRefreshBatch batch = await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        Assert.Equal(new[] { "2026R151", "2026r150" }, fetch.Branches);
        Assert.All(batch.Attempts, attempt => Assert.True(attempt.Succeeded));
        await service.RefreshDueAsync([repository], 30, Now.AddMinutes(1), false, CancellationToken.None);
        Assert.Equal(1, fetch.Discoveries);
        fetch.Advertised.Clear();
        await service.RefreshDueAsync([repository], 30, Now.AddMinutes(31), false, CancellationToken.None);
        Assert.Equal(2, fetch.Discoveries);
        Assert.All(service.Latest(), status => Assert.Equal("Remote baseline discovery", status.Branch));
        await service.RefreshDueAsync([], 30, Now.AddMinutes(32), false, CancellationToken.None);
        Assert.Empty(service.Latest());
    }

    [Fact]
    public async Task FailedDiscoveryBacksOffAndNeverFetchesAnUnverifiedInventory()
    {
        Fetcher fetch = new() { FailDiscovery = true };
        RefreshConfiguredBaselinesService service = new(new Remote(), fetch);
        GitRepositoryRegistration repository = new(Url, Path.Combine(Path.GetTempPath(), "source"), Path.Combine(Path.GetTempPath(), "root"))
        { BaselinePatterns = [BaselineBranchPolicy.DefaultPattern] };
        await service.RefreshDueAsync([repository], 30, Now, false, CancellationToken.None);
        Assert.False(Assert.Single(service.Latest()).Succeeded);
        await service.RefreshDueAsync([repository], 30, Now.AddMinutes(1), false, CancellationToken.None);
        Assert.Equal(1, fetch.Discoveries);
        Assert.Empty(fetch.Branches);
        fetch.FailDiscovery = false;
        fetch.Advertised.Add("master");
        await service.RefreshDueAsync([repository], 30, Now.AddMinutes(31), false, CancellationToken.None);
        Assert.Equal("master", Assert.Single(fetch.Branches));
    }

    [Fact]
    public void RegexPolicyUsesLinearTimeExpressionsAndEnforcesInventoryBounds()
    {
        Assert.Equal("master", Assert.Single(BaselineBranchPolicy.Match([BaselineBranchPolicy.DefaultPattern], ["master", "masterx", "other/master"])));
        Assert.Throws<InvalidOperationException>(() => BaselineBranchPolicy.Compile(["["]));
        Assert.Throws<InvalidOperationException>(() => BaselineBranchPolicy.Compile([@"^(a+)\1$"]));
        Assert.Throws<InvalidOperationException>(() => BaselineBranchPolicy.Match([".*"], Enumerable.Range(0, 257).Select(i => "branch" + i).ToArray()));
        Assert.Empty(BaselineBranchPolicy.Match([], ["master"]));
    }

    private sealed class Remote(string url = Url) : IGitRemoteReader
    {
        public Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken) => Task.FromResult(url);
    }

    private sealed class Fetcher : IGitBaselineFetcher, IGitRemoteBranchReader
    {
        public List<string> Branches { get; } = [];
        public List<string> Advertised { get; } = [];
        public int Discoveries { get; private set; }
        public bool FailDiscovery { get; set; }
        public Task<IReadOnlyList<string>> ListRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken)
        {
            Discoveries++;
            if (FailDiscovery) throw new IOException("offline");
            return Task.FromResult<IReadOnlyList<string>>(Advertised);
        }
        public Func<CancellationToken, Task>? Before { get; init; }
        public async Task<string> FetchBaselineCommitAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
        {
            Branches.Add(branch);
            if (Before is not null) await Before(cancellationToken);
            return new string('a', 40);
        }
    }
}
