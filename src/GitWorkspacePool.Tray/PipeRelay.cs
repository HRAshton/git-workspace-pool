using GitWorkspacePool.Infrastructure.Transport;

namespace GitWorkspacePool.Tray;

internal static class PipeRelay
{
    public const string PipeName = "GitWorkspacePool.Requests.v1";

    public static Task SendAsync(string message, CancellationToken cancellationToken) =>
        new NamedPipeRequestClient().SendAsync(PipeName, message, cancellationToken);
}
