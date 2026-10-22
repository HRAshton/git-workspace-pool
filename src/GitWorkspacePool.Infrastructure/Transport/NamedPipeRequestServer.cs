using System.IO.Pipes;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Transport;

/// <summary>
/// Receives bounded user-scoped messages without imposing UI dependencies.
/// </summary>
public sealed class NamedPipeRequestServer
{
    private readonly string _pipeName;

    /// <summary>
    /// Initializes a named-pipe request listener.
    /// </summary>
    public NamedPipeRequestServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
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
                    string? message = await ReadBoundedLineAsync(reader, cancellationToken);
                    if (message is { Length: > 0 })
                    {
                        onRequest(message);
                    }
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A peer may disconnect during a frame. Drop only that
                    // request and continue serving later protocol launches.
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
            if (character == '\\n')
            {
                // StreamWriter on Windows uses CRLF, but no embedded CR or
                // second command is allowed. Require the newline terminator.
                if (message.Length > 0 && message[^1] == '\\r')
                {
                    message.Length--;
                }

                return message.Length is > 0 and <= NamedPipeRequestClient.MaxMessageLength
                    && !message.ToString().Contains('\\r')
                    ? message.ToString()
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
