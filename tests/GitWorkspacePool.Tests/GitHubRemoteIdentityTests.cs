using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests;

/// <summary>
/// Pure remote identity tests: the browser cannot select a similarly named clone.
/// </summary>
public sealed class GitHubRemoteIdentityTests
{
    private static readonly PullRequestReference Request = new("github", "owner", "repo", 17);

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://github.com/OWNER/REPO")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo.git/")]
    public void Matches_AcceptsCanonicalGitHubOrigins(string url)
    {
        Assert.True(GitHubRemoteIdentity.Matches(url, Request));
    }

    [Theory]
    [InlineData("https://github.com.evil.example/owner/repo.git")]
    [InlineData("https://github.com/owner/other.git")]
    [InlineData("http://github.com/owner/repo.git")]
    [InlineData("https://token@github.com/owner/repo.git")]
    [InlineData("ssh://root@github.com/owner/repo.git")]
    [InlineData("git@evil.example:owner/repo.git")]
    [InlineData("https://github.com/owner/repo.git/another")]
    [InlineData("https://github.com/owner/repo.git?ref=main")]
    [InlineData("file:///C:/repos/repo.git")]
    [InlineData("C:\\repos\\repo.git")]
    public void Matches_RejectsDifferentOrUnsafeOrigins(string url)
    {
        Assert.False(GitHubRemoteIdentity.Matches(url, Request));
    }
}
