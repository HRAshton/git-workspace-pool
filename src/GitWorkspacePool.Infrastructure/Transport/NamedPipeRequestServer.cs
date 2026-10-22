using System.IO.Pipes;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Transport;

/// <summary>
/// Receives bounded user-scoped messages without imposing UI dependencies.
/// </summary>
public sealed class NamedPipeRequestServer
{
    private readonly string _pipeName;
    private readonly TimeSpan _frameReadTimeout;

    /// <summary>
    /// Initializes a named-pipe request listener.
    /// </summary>
    public NamedPipeRequestServer(string pipeName, TimeSpan? frameReadTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _frameReadTimeout = frameReadTimeout ?? TimeSpan.FromSeconds(5);
        if (_frameReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameReadTimeout));
        }
    }

    /// <summary>
    /// Listens until cancellation; the callback must enqueue work rather than block the listener.
    /// </summary>
    public async Task ListenAsync(Action<string> onRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onRequest);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );

                await pipe.WaitForConnectionAsync(cancellationToken);
                using StreamReader reader = new(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true
                );
                // Bound input *while reading*, not after ReadLineAsync has
                // allocated an attacker-controlled string. A caller in the
                // current user session can connect without using our client.
                try
                {
                    // The listener accepts one peer at a time. Without a
                    // deadline an idle client could block all later launches.
                    using CancellationTokenSource frameDeadline =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    frameDeadline.CancelAfter(_frameReadTimeout);
                    string? message = await ReadBoundedLineAsync(reader, frameDeadline.Token);
                    if (message is { Length: > 0 })
                    {
                        onRequest(message);
                    }
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A peer disconnected mid-frame. Continue serving.
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The per-connection read deadline elapsed. Drop this
                    // incomplete frame without stopping the pipe listener.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken
    )
    {
        StringBuilder message = new();
        char[] next = new char[1];
        int max = NamedPipeRequestClient.MaxMessageLength;

        while (await reader.ReadAsync(next.AsMemory(), cancellationToken) != 0)
        {
            char character = next[0];
            if (character == '\n')
            {
                // StreamWriter on Windows uses CRLF, but no embedded CR or
                // second command is allowed. Require the newline terminator.
                if (message.Length > 0 && message[^1] == '\r')
                {
                    message.Length--;
                }

                string frame = message.ToString();
                return frame.Length is > 0 and <= NamedPipeRequestClient.MaxMessageLength
                    && !frame.Contains('\r')
                    ? frame
                    : null;
            }

            message.Append(character);
            if (message.Length > max + 1)
            {
                // Allow just one extra character for an optional CRLF.
                return null;
            }
        }

        // EOF without a newline is not a complete protocol frame.
        return null;
    }
}
