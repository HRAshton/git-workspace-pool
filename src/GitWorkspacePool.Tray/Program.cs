using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The package smoke path must never acquire the production mutex,
        // register a protocol, start the tray, or open user settings.
        if (args.Length == 2 && args[0] == "--smoke-test")
        {
            try
            {
                string executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("No process executable path.");
                if (!OperatingSystem.IsWindows()
                    || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(executable))
                {
                    return 2;
                }

                string report = Path.GetFullPath(args[1]);
                if (!Directory.Exists(Path.GetDirectoryName(report)!))
                {
                    return 3;
                }

                // Deliberately only the caller-supplied report file is written.
                // This is a launch/dependency smoke, not a UI or Git test.
                File.WriteAllText(report, "GWP_PORTABLE_SMOKE_V1" + Environment.NewLine);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

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
                    return 1;
                }
            }

            return 0;
        }

        try
        {
            SettingsStore.EnsureCreated();
            ProtocolRegistration.RegisterCurrentUser();
            Application.Run(new TrayContext(initialRequest));
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Git Workspace Pool");
            return 1;
        }
    }
}
