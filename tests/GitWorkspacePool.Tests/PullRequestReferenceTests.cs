using GitWorkspacePool.Core.Domain;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class PullRequestReferenceTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/42/files")]
    [InlineData("https://github.com/owner/repo/pull/42/commits?expand=1")]
    public void TryParse_AcceptsGitHubPullRequestPages(string url)
    {
        bool parsed = PullRequestReference.TryParse(url, out PullRequestReference? request);

        Assert.True(parsed);
        Assert.NotNull(request);
        Assert.Equal("github", request.Provider);
        Assert.Equal("owner/repo", request.RepositoryKey);
        Assert.Equal(42, request.Number);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/pull/42")]
    [InlineData("https://github.com.evil.invalid/owner/repo/pull/42")]
    [InlineData("https://user@github.com/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/issues/42")]
    [InlineData("https://github.com/owner/repo/pull/0")]
    [InlineData("https://github.com/owner/repo/pull/not-a-number")]
    [InlineData("file:///C:/Windows/System32")]
    public void TryParse_RejectsUntrustedOrUnsupportedUrls(string url)
    {
        Assert.False(PullRequestReference.TryParse(url, out _));
    }
}
