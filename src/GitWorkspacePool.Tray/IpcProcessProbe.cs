using GitWorkspacePool.Infrastructure.Transport;

namespace GitWorkspacePool.Tray;

/// <summary>
/// Isolated Windows process E2E entry point. Never starts the tray, reads
/// settings, registers a protocol or touches a managed workspace.
/// </summary>
internal static class IpcProcessProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length < 2)
        {
            return 2;
        }

        string nonce = args[1];
        if (nonce.Length != 32 || !nonce.All(Uri.IsHexDigit))
        {
            return 2;
        }

        string mutexName = @"Local\GitWorkspacePool.IPC.E2E." + nonce;
        string pipeName = "GitWorkspacePool.IPC.E2E." + nonce;
        using Mutex mutex = SingleInstanceMutex.Acquire(mutexName, out bool firstInstance);

        if (args[0] == "--ipc-e2e-forward" && args.Length == 3)
        {
            // A forwarding process must never silently become the primary.
            if (firstInstance)
            {
                return 4;
            }

            await new NamedPipeRequestClient().SendAsync(
                pipeName, args[2], CancellationToken.None
            );
            return 0;
        }

        if (!firstInstance)
        {
            return 4;
        }

        if (args[0] == "--ipc-e2e-host" && args.Length == 4)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
            TaskCompletionSource<string> received = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Task listener = new NamedPipeRequestServer(pipeName).ListenAsync(
                message => received.TrySetResult(message), timeout.Token
            );

            // The listener is already waiting before we announce readiness.
            WriteNewFile(args[2], "READY");
            string request = await received.Task.WaitAsync(timeout.Token);
            WriteNewFile(args[3], request);
            await timeout.CancelAsync();
            await listener;
            return 0;
        }

        if (args[0] == "--ipc-e2e-hold" && args.Length == 3)
        {
            // Simulates a crashed/unready primary: mutex exists, pipe does not.
            WriteNewFile(args[2], "READY");
            await Task.Delay(TimeSpan.FromSeconds(20));
            return 0;
        }

        return 2;
    }

    private static void WriteNewFile(string path, string content)
    {
        using FileStream stream = new(
            Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None
        );
        using StreamWriter writer = new(stream);
        writer.Write(content);
    }
}
