using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? initialRequest = args.Length == 1 ? args[0] : null;
        using Mutex mutex = new(
            initiallyOwned: true,
            name: @"Local\GitWorkspacePool.Tray.v1",
            createdNew: out bool firstInstance
        );

        if (!firstInstance)
        {
            if (initialRequest is not null)
            {
                try
                {
                    PipeRelay
                        .SendAsync(initialRequest, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, "Git Workspace Pool");
                }
            }

            return;
        }

        try
        {
            SettingsStore.EnsureCreated();
            ProtocolRegistration.RegisterCurrentUser();
            Application.Run(new TrayContext(initialRequest));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Git Workspace Pool");
        }
    }
}
