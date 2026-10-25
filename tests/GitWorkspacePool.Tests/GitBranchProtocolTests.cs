using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Protocol;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class GitBranchProtocolTests
{
    private const string Repo = "https://bitbucket.example.com:8443/stash/scm/team/repo.git";

    [Theory]
    [InlineData("feature/task-42")]
    [InlineData("release/9.0")]
    [InlineData("feature/日本語")]
    [InlineData("feature/a+b")]
    [InlineData("feature/percent%20literal")]
    public void Protocol_RoundTripsRepoAndCaseSensitiveBranchWithoutPr(string branch)
    {
        Assert.True(GitBranchProtocol.TryParse(Link(Repo, branch), out GitBranchRequest? request));
        Assert.Equal(Repo, request.CloneUrl);
        Assert.Equal(branch, request.Branch);
    }

    [Theory]
    [InlineData("http://bitbucket.example.com/scm/team/repo.git")]
    [InlineData("file:///tmp/repo.git")]
    [InlineData("https://user:password@bitbucket.example.com/scm/team/repo.git")]
    [InlineData("https://user@bitbucket.example.com/scm/team/repo.git")]
    [InlineData("https://bitbucket.example.com/scm/team/repo.git?token=secret")]
    [InlineData("https://bitbucket.example.com/scm/team/repo.git#fragment")]
    [InlineData("https://bitbucket.example.com/")]
    public void Protocol_RejectsUnsafeCloneUrls(string repo) =>
        Assert.False(GitBranchProtocol.TryParse(Link(repo, "main"), out _));

    [Theory]
    [InlineData("")]
    [InlineData("bad branch")]
    [InlineData("feature:refs/heads/main")]
    [InlineData("../main")]
    [InlineData("main\n--upload-pack=evil")]
    [InlineData("feature//task")]
    [InlineData("feature/.hidden")]
    [InlineData("feature/task.lock")]
    [InlineData("feature/end.")]
    [InlineData("feature/@{1}")]
    [InlineData("feature/*")]
    [InlineData("feature/evil\\path")]
    public void Protocol_RejectsInvalidOrRefspecBranches(string branch) =>
        Assert.False(GitBranchProtocol.TryParse(Link(Repo, branch), out _));

    [Theory]
    [InlineData("&repo=duplicate")]
    [InlineData("&branch=main")]
    [InlineData("&v=2")]
    [InlineData("&command=delete")]
    [InlineData("#fragment")]
    public void Protocol_RejectsDuplicateUnknownOrFragmentFields(string suffix) =>
        Assert.False(GitBranchProtocol.TryParse(Link(Repo, "main") + suffix, out _));

    [Theory]
    [InlineData("prworkspace://open:123/?v=2&repo=x&branch=main")]
    [InlineData("prworkspace://user@open/?v=2&repo=x&branch=main")]
    [InlineData("prworkspace://open/path?v=2&repo=x&branch=main")]
    public void Protocol_RejectsAlteredEndpoint(string link) =>
        Assert.False(GitBranchProtocol.TryParse(link, out _));

    [Fact]
    public void Identity_PreservesBranchAndRepositoryPathCaseAndSeparatesServers()
    {
        GitBranchRequest main = new(Repo, "feature/Work");
        Assert.NotEqual(main.WorkspaceName, new GitBranchRequest(Repo, "feature/work").WorkspaceName);
        Assert.NotEqual(main.WorkspaceName, new GitBranchRequest(Repo.Replace("repo.git", "Repo.git"), main.Branch).WorkspaceName);
        Assert.NotEqual(main.WorkspaceName, new GitBranchRequest(Repo.Replace("example.com", "other.com"), main.Branch).WorkspaceName);
        Assert.Equal(main.WorkspaceName, new GitBranchRequest(Repo + "/", main.Branch).WorkspaceName);
        Assert.True(GitBranchRequest.OriginMatches(Repo.Replace("https://", "https://alice@"), Repo));
        Assert.False(GitBranchRequest.OriginMatches(Repo.Replace("https://", "https://alice%3Asecret@"), Repo));
        Assert.False(GitBranchRequest.OriginMatches(Repo.Replace("example.com", "example.com.evil"), Repo));
    }

    [Theory]
    [InlineData("ssh://git@bitbucket.example.com:7999/team/repo.git")]
    [InlineData("git@bitbucket.example.com:team/repo.git")]
    public void Protocol_RoundTripsSshAndKeepsUserAndPathIdentity(string repo)
    {
        Assert.True(GitBranchProtocol.TryParse(Link(repo, "feature/work"), out GitBranchRequest? request));
        Assert.Equal(repo, request.CloneUrl);
        Assert.True(GitBranchRequest.OriginMatches(repo, repo));
        Assert.False(GitBranchRequest.OriginMatches(repo.Replace("git@", "other@"), repo));
    }

    [Theory]
    [InlineData("ssh://git:password@host/team/repo.git")]
    [InlineData("ssh://-option@host/team/repo.git")]
    [InlineData("git@host:repo;command")]
    [InlineData("git@host:-option")]
    [InlineData("ssh://git@host/repo%0a")]
    public void Protocol_RejectsUnsafeSsh(string repo)
    {
        Assert.False(new GitBranchRequest(repo, "main").IsValid);
    }

    [Fact]
    public void SshIdentityDoesNotEquateConfiguredPortWithExplicitPort22()
    {
        const string configured = "ssh://git@host/team/repo.git";
        const string explicitPort = "ssh://git@host:22/team/repo.git";
        Assert.NotEqual(GitBranchRequest.GetRepositoryIdentity(configured), GitBranchRequest.GetRepositoryIdentity(explicitPort));
        Assert.False(GitBranchRequest.OriginMatches(configured, explicitPort));
    }

    public static string Link(string repo, string branch) =>
        "prworkspace://open/?v=2&repo=" + Uri.EscapeDataString(repo) + "&branch=" + Uri.EscapeDataString(branch);
}
