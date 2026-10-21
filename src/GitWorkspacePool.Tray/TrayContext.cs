using System.Drawing;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;
using GitWorkspacePool.Infrastructure.Transport;
using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OpenPullRequestHandler _open;
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly CaptureWorkspaceSnapshotService _snapshot;

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

        string stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitWorkspacePool"
        );
        GitCliWorkspaceGit git = new();
        SqliteWorkspaceSessionJournal journal = new(Path.Combine(stateRoot, "sessions.db"));
        FileWorkspaceOperationLock locks = new(Path.Combine(stateRoot, "locks"));
        _sessions = journal;
        _snapshot = new CaptureWorkspaceSnapshotService(
            journal,
            locks,
            git,
            new VerifiedWorkspaceSnapshotExporter(
                Path.Combine(stateRoot, "snapshots"),
                new GitWorktreeSnapshotScanner()
            )
        );
        WorkspaceAcquisitionService acquisition = new(
            git,
            new WorkspaceDirectories(),
            journal,
            locks
        );
        _open = new OpenPullRequestHandler(
            new SettingsStore(),
            new GitHubOriginVerifier(git),
            acquisition,
            new VsCodeLauncher()
        );

        ToolStripMenuItem snapshotsMenu = new("Snapshot active PR...");
        snapshotsMenu.DropDownOpening += async (_, _) => await PopulateSnapshotsAsync(snapshotsMenu);
        menu.Items.Insert(1, snapshotsMenu);

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

    private async Task PopulateSnapshotsAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();

        try
        {
            IReadOnlyList<WorkspaceSession> active = await _sessions.ListActiveAsync(
                _shutdown.Token
            );

            if (active.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No active PR sessions")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in active)
            {
                ToolStripMenuItem item = new(
                    $"{session.RepositoryKey} PR #{session.PullRequestNumber}"
                );
                Guid operationId = session.OperationId;
                item.Click += (_, _) => _ = CaptureFromMenuAsync(operationId);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list active sessions", exception.Message);
        }
    }

    private async Task CaptureFromMenuAsync(Guid operationId)
    {
        try
        {
            WorkspaceSnapshotReceipt receipt = await _snapshot.CaptureAsync(
                operationId,
                _shutdown.Token
            );
            Notify("Workspace snapshot saved", receipt.Folder);
        }
        catch (OperationCanceledException)
        {
            // Explicit cancellation or application shutdown.
        }
        catch (Exception exception)
        {
            Notify("Snapshot not published", exception.Message);
        }
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
            NamedPipeRequestServer server = new(PipeRelay.PipeName);
            await server.ListenAsync(message => _ = HandleRequestAsync(message), cancellationToken);
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
