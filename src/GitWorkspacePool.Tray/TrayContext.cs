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
    private readonly RestoreWorkspaceSnapshotService _restore;
    private readonly IWorkspaceRecoveryInspector _recoveryInspector;
    private readonly SuspendWorkspaceSessionService _suspend;
    private readonly ParkWorkspaceSessionService _parking;
    private readonly IWorkspaceParkingJournal _parkingJournal;

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
        _parkingJournal = journal;
        _parking = new ParkWorkspaceSessionService(
            journal,
            journal,
            locks,
            new GitWorkspaceParkingOperations(git)
        );
        _suspend = new SuspendWorkspaceSessionService(
            journal,
            journal,
            locks,
            git,
            new WorkspaceDirectories()
        );
        _snapshot = new CaptureWorkspaceSnapshotService(
            journal,
            locks,
            git,
            new VerifiedWorkspaceSnapshotExporter(
                Path.Combine(stateRoot, "snapshots"),
                new GitWorktreeSnapshotScanner()
            )
        );
        _restore = new RestoreWorkspaceSnapshotService(
            journal,
            new VerifiedWorkspaceSnapshotRestorer(
                Path.Combine(stateRoot, "recovered"),
                git,
                new GitWorktreeSnapshotScanner(),
                journal
            )
        );
        _recoveryInspector = new WorkspaceRecoveryInspector(
            Path.Combine(stateRoot, "snapshots"),
            Path.Combine(stateRoot, "recovered"),
            journal,
            git,
            new SqliteWorkspaceSlotRegistry(Path.Combine(stateRoot, "sessions.db"))
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

        ToolStripMenuItem restoreMenu = new("Restore snapshot to new worktree...");
        restoreMenu.DropDownOpening += async (_, _) => await PopulateRestoresAsync(restoreMenu);
        menu.Items.Insert(2, restoreMenu);

        ToolStripMenuItem recoveryMenu = new("Recovery diagnostics...");
        recoveryMenu.DropDownOpening += async (_, _) => await PopulateRecoveryDiagnosticsAsync(recoveryMenu);
        menu.Items.Insert(3, recoveryMenu);

        ToolStripMenuItem suspendMenu = new("Suspend active PR (keep worktree)...");
        suspendMenu.DropDownOpening += async (_, _) => await PopulateSuspensionsAsync(suspendMenu);
        menu.Items.Insert(4, suspendMenu);

        ToolStripMenuItem parkMenu = new("Park PR worktree (move, no deletion)...");
        parkMenu.DropDownOpening += async (_, _) => await PopulateParkingAsync(parkMenu, false);
        menu.Items.Insert(5, parkMenu);

        ToolStripMenuItem unparkMenu = new("Unpark PR worktree...");
        unparkMenu.DropDownOpening += async (_, _) => await PopulateParkingAsync(unparkMenu, true);
        menu.Items.Insert(6, unparkMenu);

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

    private async Task PopulateRestoresAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();

        try
        {
            IReadOnlyList<WorkspaceSnapshotRecord> snapshots = await _sessions.ListSnapshotsAsync(
                _shutdown.Token
            );

            if (snapshots.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No published snapshots")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSnapshotRecord record in snapshots)
            {
                ToolStripMenuItem item = new(
                    $"{record.Session.RepositoryKey} PR #{record.Session.PullRequestNumber} "
                        + $"({record.Receipt.SnapshotId.ToString("N")[..8]})"
                );
                Guid snapshotId = record.Receipt.SnapshotId;
                item.Click += (_, _) => _ = RestoreFromMenuAsync(snapshotId);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list snapshots", exception.Message);
        }
    }

    private async Task RestoreFromMenuAsync(Guid snapshotId)
    {
        DialogResult decision = MessageBox.Show(
            "Restore this snapshot into a separate, new Git worktree? "
                + "Existing PR sessions will not be changed.",
            "Restore workspace snapshot",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (decision != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string path = await _restore.RestoreAsync(snapshotId, _shutdown.Token);
            Notify("Recovery worktree created", path);
        }
        catch (OperationCanceledException)
        {
            // User cancelled or application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Recovery could not be verified", exception.Message);
        }
    }

    private async Task PopulateRecoveryDiagnosticsAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();

        try
        {
            IReadOnlyList<WorkspaceRecoveryFinding> findings =
                await _recoveryInspector.ScanAsync(_shutdown.Token);

            if (findings.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No recovery issues found")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceRecoveryFinding finding in findings.Take(25))
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    $"{finding.Kind}: {Path.GetFileName(finding.Path)}"
                )
                {
                    ToolTipText = finding.Description + Environment.NewLine + finding.Path,
                    Enabled = false,
                });
            }

            if (findings.Count > 25)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    $"{findings.Count - 25} more entries require inspection"
                )
                {
                    Enabled = false,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Recovery inspection failed", exception.Message);
        }
    }

    private async Task PopulateSuspensionsAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();

        try
        {
            IReadOnlyList<WorkspaceSession> sessions = await _sessions.ListActiveAsync(
                _shutdown.Token
            );

            if (sessions.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No active PR sessions")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in sessions)
            {
                ToolStripMenuItem item = new(
                    $"{session.RepositoryKey} PR #{session.PullRequestNumber}"
                );
                Guid operationId = session.OperationId;
                item.Click += (_, _) => _ = SuspendFromMenuAsync(operationId);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list sessions", exception.Message);
        }
    }

    private async Task SuspendFromMenuAsync(Guid operationId)
    {
        DialogResult result = MessageBox.Show(
            "Suspend this PR session? Its existing worktree and all changes will stay "
                + "exactly where they are. This will NOT free disk space or make "
                + "the slot available for reuse. Opening the PR again resumes it.",
            "Suspend PR session",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string path = await _suspend.SuspendAsync(operationId, _shutdown.Token);
            Notify("PR suspended; files preserved", path);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            Notify("Could not suspend PR", exception.Message);
        }
    }

    private async Task PopulateParkingAsync(ToolStripMenuItem menu, bool unpark)
    {
        menu.DropDownItems.Clear();

        try
        {
            IReadOnlyList<WorkspaceSession> sessions = unpark
                ? await _parkingJournal.ListUnparkCandidatesAsync(_shutdown.Token)
                : await _parkingJournal.ListParkCandidatesAsync(_shutdown.Token);

            if (sessions.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No eligible PR sessions")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in sessions)
            {
                Guid id = session.OperationId;
                ToolStripMenuItem item = new(
                    $"{session.RepositoryKey} PR #{session.PullRequestNumber} ({session.State})"
                );
                item.Click += (_, _) => _ = MoveParkingFromMenuAsync(id, unpark);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list parked sessions", exception.Message);
        }
    }

    private async Task MoveParkingFromMenuAsync(Guid id, bool unpark)
    {
        DialogResult choice = MessageBox.Show(
            unpark
                ? "Return the parked Git worktree to its original path? "
                    + "Occupied destinations will never be overwritten."
                : "Move the complete PR worktree into private parking under the "
                    + "same workspace root? All changes are preserved in the moved "
                    + "directory. Nothing is deleted.",
            unpark ? "Unpark PR worktree" : "Park PR worktree",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (choice != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string path = unpark
                ? await _parking.UnparkAsync(id, _shutdown.Token)
                : await _parking.ParkAsync(id, _shutdown.Token);
            Notify(unpark ? "Worktree returned" : "Worktree parked", path);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            Notify("Worktree relocation was not verified", exception.Message);
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
