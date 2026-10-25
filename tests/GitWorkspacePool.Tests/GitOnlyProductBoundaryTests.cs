using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class GitOnlyProductBoundaryTests
{
    [Fact]
    public void ShippedLibrariesContainNoProviderAdaptersOrProviderFetchContract()
    {
        Assert.DoesNotContain(typeof(GitCliWorkspaceGit).Assembly.GetTypes(), type => type.Name.Contains("GitHub", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(OpenGitBranchHandler).Assembly.GetTypes(), type => type.Name.Contains("PullRequest", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IWorkspaceGit).GetMethods(), method => method.Name.Contains("PullRequest", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(GitCliWorkspaceGit).Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "GitWorkspacePool.LegacyTestSupport");
        Assert.DoesNotContain(typeof(OpenGitBranchHandler).Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "GitWorkspacePool.LegacyTestSupport");
    }
}
