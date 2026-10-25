using System.IO.Pipes;
using System.Text;
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
        string pullRequest = "https://github.com/owner/repo.git";
        string message = GitBranchProtocolTests.Link(pullRequest, "feature/task");

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

        Assert.True(GitBranchProtocol.TryParse(forwarded, out var parsed));
        string requestUrl = parsed.CloneUrl;
        Assert.Equal(pullRequest, requestUrl);

        await shutdown.CancelAsync();
        await listener;
    }


    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task NamedPipe_RejectsOversizedFrameWithoutStoppingListener()
    {
        string name = "GitWorkspacePool.E2E." + Guid.NewGuid().ToString("N");
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> accepted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int dispatched = 0;
        Task listener = new NamedPipeRequestServer(name).ListenAsync(
            payload =>
            {
                Interlocked.Increment(ref dispatched);
                accepted.TrySetResult(payload);
            },
            shutdown.Token
        );

        try
        {
            using (NamedPipeClientStream raw = new(
                ".", name, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            ))
            {
                await raw.ConnectAsync(5000, shutdown.Token);
                using StreamWriter writer = new(raw, new UTF8Encoding(false), leaveOpen: true);
                try
                {
                    await writer.WriteLineAsync(
                        new string('x', NamedPipeRequestClient.MaxMessageLength + 2)
                    );
                    await writer.FlushAsync(shutdown.Token);
                }
                catch (IOException)
                {
                    // The listener may reject and close the overlong frame
                    // before this deliberately untrusted client finishes.
                }
            }

            string valid = "prworkspace://open?url="
                + Uri.EscapeDataString("https://github.com/owner/repo/pull/17")
                + "&v=1";
            await new NamedPipeRequestClient().SendAsync(name, valid, shutdown.Token);

            Assert.Equal(valid, await accepted.Task.WaitAsync(shutdown.Token));
            Assert.Equal(1, Volatile.Read(ref dispatched));
        }
        finally
        {
            await shutdown.CancelAsync();
            await listener;
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task NamedPipe_RejectsUnterminatedFrameAndAcceptsNextRequest()
    {
        string name = "GitWorkspacePool.E2E." + Guid.NewGuid().ToString("N");
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> accepted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int dispatched = 0;
        Task listener = new NamedPipeRequestServer(name).ListenAsync(
            payload =>
            {
                Interlocked.Increment(ref dispatched);
                accepted.TrySetResult(payload);
            },
            shutdown.Token
        );

        try
        {
            using (NamedPipeClientStream raw = new(
                ".", name, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            ))
            {
                await raw.ConnectAsync(5000, shutdown.Token);
                using StreamWriter writer = new(raw, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync("not-a-complete-protocol-frame");
                await writer.FlushAsync(shutdown.Token);
            }

            string valid = "prworkspace://open?url="
                + Uri.EscapeDataString("https://github.com/owner/repo/pull/17")
                + "&v=1";
            await new NamedPipeRequestClient().SendAsync(name, valid, shutdown.Token);

            Assert.Equal(valid, await accepted.Task.WaitAsync(shutdown.Token));
            Assert.Equal(1, Volatile.Read(ref dispatched));
        }
        finally
        {
            await shutdown.CancelAsync();
            await listener;
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task NamedPipe_IdlePeerTimesOutWithoutStarvingNextProtocolRequest()
    {
        string name = "GitWorkspacePool.E2E." + Guid.NewGuid().ToString("N");
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource<string> accepted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int dispatched = 0;
        Task listener = new NamedPipeRequestServer(
            name, TimeSpan.FromMilliseconds(250)
        ).ListenAsync(
            payload =>
            {
                Interlocked.Increment(ref dispatched);
                accepted.TrySetResult(payload);
            },
            shutdown.Token
        );

        try
        {
            using NamedPipeClientStream idle = new(
                ".", name, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );
            await idle.ConnectAsync(5000, shutdown.Token);
            byte[] fragment = Encoding.UTF8.GetBytes("prworkspace://open?url=unfinished");
            await idle.WriteAsync(fragment, shutdown.Token);
            await idle.FlushAsync(shutdown.Token);

            // Keep the first connection open, deliberately omitting newline.
            // A second launcher must be able to reach the listener after the
            // frame deadline, without waiting for the idle peer to disconnect.
            string valid = "prworkspace://open?url="
                + Uri.EscapeDataString("https://github.com/owner/repo/pull/17")
                + "&v=1";
            await new NamedPipeRequestClient().SendAsync(name, valid, shutdown.Token);

            Assert.Equal(valid, await accepted.Task.WaitAsync(shutdown.Token));
            Assert.Equal(1, Volatile.Read(ref dispatched));
        }
        finally
        {
            await shutdown.CancelAsync();
            await listener;
        }
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
