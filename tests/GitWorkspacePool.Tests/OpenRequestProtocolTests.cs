using GitWorkspacePool.Core.Protocol;
using Xunit;

namespace GitWorkspacePool.Tests;

public sealed class OpenRequestProtocolTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/pull/17")]
    [InlineData("https://github.com/owner/repo/pull/17/files?diff=1")]
    public void TryExtract_AcceptsVersionedGitHubPullRequest(string pullRequest)
    {
        string protocolUrl = "prworkspace://open?url="
            + Uri.EscapeDataString(pullRequest)
            + "&v=1";

        Assert.True(OpenRequestProtocol.TryExtract(protocolUrl, out string extracted));
        Assert.Equal(pullRequest, extracted);
    }

    [Theory]
    [InlineData("prworkspace://open?v=1")]
    [InlineData("prworkspace://open?url=https%3A%2F%2Fgithub.com%2Fa%2Fb%2Fpull%2F1&v=2")]
    [InlineData("prworkspace://open?url=not-a-url&v=1")]
    [InlineData("prworkspace://open?url=file%3A%2F%2F%2FC%3A%2Fsecret&v=1")]
    [InlineData("prworkspace://open?url=https%3A%2F%2Fgithub.com%2Fa%2Fb%2Fpull%2F1&v=1&v=1")]
    [InlineData("prworkspace://open?url=https%3A%2F%2Fgithub.com%2Fa%2Fb%2Fpull%2F1&v=1&cmd=delete")]
    [InlineData("prworkspace://evil?url=https%3A%2F%2Fgithub.com%2Fa%2Fb%2Fpull%2F1&v=1")]
    public void TryExtract_RejectsUntrustedProtocolPayload(string value)
    {
        Assert.False(OpenRequestProtocol.TryExtract(value, out _));
    }
}
