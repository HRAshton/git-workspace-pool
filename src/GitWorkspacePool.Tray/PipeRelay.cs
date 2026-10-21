using System.IO.Pipes;
using System.Text;

namespace GitWorkspacePool.Tray;

internal static class PipeRelay
{
    public const string PipeName = "GitWorkspacePool.Requests.v1";

    public static async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using NamedPipeClientStream client = new(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous
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
