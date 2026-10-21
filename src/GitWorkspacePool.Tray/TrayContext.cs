using System.Drawing;
using System.IO.Pipes;
using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OpenPullRequestHandler _open;

    public TrayContext(string? initialRequest)
    {
        _dispatcher = new Control();
        _ = _dispatcher.Handle;

        ContextMenuStrip menu = new();
        menu.Items.Add("Open settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Git Workspace Pool",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _open = new OpenPullRequestHandler(
            new SettingsStore(),
            new GitCliWorkspaceGit(),
            new WorkspaceDirectories(),
            new VsCodeLauncher()
        );

        _ = ListenAsync(_shutdown.Token);

        if (initialRequest is not null)
        {
            _ = HandleRequestAsync(initialRequest);
        }
    }

    protected override void ExitThreadCore()
    {
        _shutdown.Cancel();
        _icon.Visible = false;
        _icon.Dispose();
        _dispatcher.Dispose();
        _shutdown.Dispose();
        base.ExitThreadCore();
    }

    private static void OpenSettings()
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(SettingsStore.FilePath)
            {
                UseShellExecute = true,
            }
        );
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using NamedPipeServerStream pipe = new(
                    PipeRelay.PipeName,
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

                if (message is not null && message.Length <= 8192)
                {
                    _ = HandleRequestAsync(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
        catch (IOException exception)
        {
            Notify("IPC error", exception.Message);
        }
    }

    private async Task HandleRequestAsync(string raw)
    {
        if (!OpenRequestProtocol.TryExtract(raw, out string requestUrl))
        {
            Notify("Rejected URL", "Only configured GitHub pull requests are accepted.");
            return;
        }

        try
        {
            OpenPullRequestResult result = await _open.HandleAsync(
                requestUrl,
                _shutdown.Token
            );
            Notify(
                result.Reused ? "Reopened PR workspace" : "Created PR workspace",
                result.WorkspacePath
            );
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
        catch (Exception exception)
        {
            Notify("Could not open PR", exception.Message);
        }
    }

    private void Notify(string title, string description)
    {
        if (!_dispatcher.IsHandleCreated || _dispatcher.IsDisposed)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                (Action)(
                    () =>
                    {
                        if (!_dispatcher.IsDisposed)
                        {
                            _icon.ShowBalloonTip(
                                4000,
                                title,
                                description,
                                ToolTipIcon.Info
                            );
                        }
                    }
                )
            );
        }
        catch (InvalidOperationException)
        {
            // Ignore notifications during application shutdown.
        }
    }
}
