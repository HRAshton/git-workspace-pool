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
                string? message = await reader.ReadLineAsync(cancellationToken);

                if (message is { Length: > 0 and <= NamedPipeRequestClient.MaxMessageLength })
                {
                    onRequest(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
