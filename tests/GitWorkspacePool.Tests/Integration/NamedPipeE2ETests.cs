using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure.Transport;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// In-process, real OS named-pipe E2E without a browser or WinForms message loop.
/// </summary>
public sealed class NamedPipeE2ETests
{
    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task NamedPipe_TransportsBrowserProtocolRequestToReceiver()
    {
        string name = "GitWorkspacePool.E2E." + Guid.NewGuid().ToString("N");
        string pullRequest = "https://github.com/owner/repo/pull/17";
        string message = "prworkspace://open?url="
            + Uri.EscapeDataString(pullRequest)
            + "&v=1";

        TaskCompletionSource<string> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));

        NamedPipeRequestServer server = new(name);
        Task listener = server.ListenAsync(
            payload => received.TrySetResult(payload),
            shutdown.Token
        );

        NamedPipeRequestClient client = new();
        await client.SendAsync(name, message, shutdown.Token);
        string forwarded = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(OpenRequestProtocol.TryExtract(forwarded, out string requestUrl));
        Assert.Equal(pullRequest, requestUrl);

        await shutdown.CancelAsync();
        await listener;
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task NamedPipe_RejectsUnframedNewlines()
    {
        NamedPipeRequestClient client = new();
        await Assert.ThrowsAsync<ArgumentException>(
            () =>
                client.SendAsync(
                    "unused",
                    "prworkspace://open?url=hello\nsecond-command",
                    CancellationToken.None
                )
        );
    }
}
