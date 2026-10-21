using System.IO.Pipes;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Transport;

/// <summary>
/// Forwards one versioned protocol request to the running tray application.
/// </summary>
public sealed class NamedPipeRequestClient
{
    /// <summary>
    /// Maximum length of a single request, in characters.
    /// </summary>
    public const int MaxMessageLength = 8192;

    /// <summary>
    /// Sends a message to the per-user named-pipe listener.
    /// </summary>
    public async Task SendAsync(
        string pipeName,
        string message,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (
            message.Length > MaxMessageLength
            || message.Contains('\n')
            || message.Contains('\r')
        )
        {
            throw new ArgumentException("Invalid protocol message size or framing.", nameof(message));
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            using NamedPipeClientStream client = new(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );

            try
            {
                await client.ConnectAsync(400, cancellationToken);

                using StreamWriter writer = new(
                    client,
                    new UTF8Encoding(false),
                    leaveOpen: true
                );
                await writer.WriteLineAsync(message.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                return;
            }
            catch (TimeoutException) when (attempt < 9)
            {
                await Task.Delay(150, cancellationToken);
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }
}
