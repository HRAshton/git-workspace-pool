using System.Drawing;
using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Diagnostics;
using GitWorkspacePool.Infrastructure.Snapshots;
using GitWorkspacePool.Infrastructure.Transport;
using GitWorkspacePool.Tray.Infrastructure;
using GitWorkspacePool.Tray.Settings;

namespace GitWorkspacePool.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OpenGitBranchHandler _openBranch;
    private readonly LocalOperationTimings _timings;
    private readonly PlanGitBranchReuseService _reusePlan;
    private readonly RefreshConfiguredBaselinesService _baselines;
    private readonly System.Windows.Forms.Timer _baselineTimer;
    private bool _baselineSettingsErrorShown;
    private readonly IGitBranchSessionStore _branchSessions;
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceRecoveryJournal _recoverySessions;
    private readonly TransferVerifiedWarmArtifactsService _warmTransfer;
    private readonly CaptureWorkspaceSnapshotService _snapshot;
    private readonly RestoreWorkspaceSnapshotService _restore;
    private readonly IWorkspaceRecoveryInspector _recoveryInspector;
    private readonly SuspendWorkspaceSessionService _suspend;
    private readonly ParkWorkspaceSessionService _parking;
    private readonly OpenReleasedParkedWorkspaceService _openRetained;
    private readonly IWorkspaceParkingJournal _parkingJournal;
    private readonly IWorkspaceSlotReservationInventory _pendingSlotInventory;
    private readonly IWorkspaceReadySlotCatalog _readyCatalog;
    private readonly IWorkspaceSlotRegistry _slotRegistry;
    private readonly IWorkspaceSlotReleasePreparationJournal _releasePreparations;
    private readonly PrepareWorkspaceSlotReleaseService _prepareRelease;
    private readonly FinalizeWorkspaceSlotReleaseService _finalizeRelease;
    private readonly ReconcileExplicitReadySlotService _reconcileSlot;
    private readonly IEditorLauncher _editor;

    public TrayContext(string? initialRequest)
    {
        _dispatcher = new Control();
        _ = _dispatcher.Handle;

        ContextMenuStrip menu = new();
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Local operation timings...", null, (_, _) => OpenTimings());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Git Workspace Pool",
            ContextMenuStrip = menu,
            Visible = true,
        };

        string stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitWorkspacePool"
        );
        _timings = new LocalOperationTimings(Path.Combine(stateRoot, "diagnostics", "timings.jsonl"));
        GitCliWorkspaceGit git = new();
        SqliteWorkspaceSessionJournal journal = new(Path.Combine(stateRoot, "sessions.db"));
        FileWorkspaceOperationLock locks = new(Path.Combine(stateRoot, "locks"));
        _sessions = journal;
        _recoverySessions = journal;
        _warmTransfer = new TransferVerifiedWarmArtifactsService(
            journal, locks, git, new VerifiedNpmArtifactCopier()
        );
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
                new GitWorktreeSnapshotScanner(),
                limits: SettingsStore.Read().SnapshotLimits
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
        SqliteWorkspaceSlotRegistry slots = new(Path.Combine(stateRoot, "sessions.db"));
        _pendingSlotInventory = slots;
        _readyCatalog = slots;
        _slotRegistry = slots;
        _releasePreparations = slots;
        AssessWorkspaceReleaseService releaseAssessment = new(
            journal, slots, locks, new GitWorkspaceParkingOperations(git)
        );
        _prepareRelease = new PrepareWorkspaceSlotReleaseService(
            journal, locks, releaseAssessment, slots
        );
        _finalizeRelease = new FinalizeWorkspaceSlotReleaseService(
            journal, locks, releaseAssessment, slots
        );
        _recoveryInspector = new WorkspaceRecoveryInspector(
            Path.Combine(stateRoot, "snapshots"),
            Path.Combine(stateRoot, "recovered"),
            journal,
            git,
            slots
        );
        SettingsStore repositories = new();
        VsCodeLauncher editor = new();
        _editor = editor;
        _reconcileSlot = new ReconcileExplicitReadySlotService(
            slots, slots, new WorkspaceReadySlotSafety(),
            new VerifiedReadySlotCheckout(), git, locks
        );
        _openRetained = new OpenReleasedParkedWorkspaceService(
            journal, locks, new GitWorkspaceParkingOperations(git), editor
        );
        AcquireExplicitReadySlotService readyAcquisition = new(
            slots, slots, new WorkspaceReadySlotSafety(), git,
            new WorkspaceDirectories(), journal, locks
        );
        _branchSessions = new SqliteGitBranchSessionStore(Path.Combine(stateRoot, "sessions.db"));
        _openBranch = new OpenGitBranchHandler(
            repositories, git, git, git, _branchSessions, locks, new WorkspaceDirectories(),
            new WorkspaceReadySlotSafety(), new VerifiedReadySlotCheckout(), editor, _openRetained, readyAcquisition
        );
        menu.Items.Insert(1, new ToolStripMenuItem("Open Git branch...", null,
            async (_, _) => await OpenGitBranchFromTrayAsync()));
        ToolStripMenuItem branchRecovery = new("Git branch recovery (read-only)...");
        branchRecovery.DropDownOpening += async (_, _) => await PopulateBranchRecoveryAsync(branchRecovery);
        menu.Items.Insert(2, branchRecovery);

        _reusePlan = new PlanGitBranchReuseService(repositories, _branchSessions, git, git,
            new VerifiedReuseCandidateCollector(slots, journal, git, new GitWorkspaceParkingOperations(git), new WorkspaceReadySlotSafety()));
        _baselines = new RefreshConfiguredBaselinesService(git, git);
        menu.Items.Insert(1, new ToolStripMenuItem("Open branch with reuse choices...", null, async (_, _) =>
        {
            try
            {
                GitBranchRequest? request = AskForGitBranch();
                if (request is not null) await OpenBranchWithReuseAsync(request);
            }
            catch (Exception exception) { Notify("Could not open branch with reuse", exception.Message); }
        }));
        menu.Items.Add("Refresh configured baselines now...", null, async (_, _) => await RefreshBaselinesAsync(true));
        ToolStripMenuItem baselineStatus = new("Configured baseline status...");
        baselineStatus.DropDownOpening += (_, _) => PopulateBaselineStatus(baselineStatus);
        menu.Items.Add(baselineStatus);

        ToolStripMenuItem snapshotsMenu = new("Snapshot active workspace...");
        snapshotsMenu.DropDownOpening += async (_, _) => await PopulateSnapshotsAsync(snapshotsMenu);
        menu.Items.Insert(1, snapshotsMenu);

        ToolStripMenuItem restoreMenu = new("Restore snapshot to new worktree...");
        restoreMenu.DropDownOpening += async (_, _) => await PopulateRestoresAsync(restoreMenu);
        menu.Items.Insert(2, restoreMenu);

        ToolStripMenuItem recoveryMenu = new("Recovery diagnostics...");
        recoveryMenu.DropDownOpening += async (_, _) => await PopulateRecoveryDiagnosticsAsync(recoveryMenu);
        menu.Items.Insert(3, recoveryMenu);

        ToolStripMenuItem diskUsageMenu = new("Local disk usage (read-only)...");
        diskUsageMenu.DropDownOpening += async (_, _) =>
            await PopulateLocalDiskUsageAsync(diskUsageMenu, stateRoot);
        menu.Items.Insert(4, diskUsageMenu);

        ToolStripMenuItem suspendMenu = new("Suspend workspace (keep worktree)...");
        suspendMenu.DropDownOpening += async (_, _) => await PopulateSuspensionsAsync(suspendMenu);
        menu.Items.Insert(4, suspendMenu);

        ToolStripMenuItem parkMenu = new("Park workspace (move, no deletion)...");
        parkMenu.DropDownOpening += async (_, _) => await PopulateParkingAsync(parkMenu, false);
        menu.Items.Insert(5, parkMenu);

        ToolStripMenuItem unparkMenu = new("Unpark workspace...");
        unparkMenu.DropDownOpening += async (_, _) => await PopulateParkingAsync(unparkMenu, true);
        menu.Items.Insert(6, unparkMenu);

        ToolStripMenuItem releaseMenu = new("Release parked slot (keep user files)...");
        releaseMenu.DropDownOpening += async (_, _) =>
            await PopulateReleaseCandidatesAsync(releaseMenu);
        menu.Items.Insert(7, releaseMenu);

        ToolStripMenuItem readyMenu = new("Open workspace in a released slot...");
        readyMenu.DropDownOpening += async (_, _) =>
            await PopulateReadySlotsAsync(readyMenu);
        menu.Items.Insert(8, readyMenu);

        ToolStripMenuItem cancelReleaseMenu = new("Cancel pending slot release...");
        cancelReleaseMenu.DropDownOpening += async (_, _) =>
            await PopulatePendingReleasesAsync(cancelReleaseMenu);
        menu.Items.Insert(9, cancelReleaseMenu);

        ToolStripMenuItem npmWarmMenu = new(
            "Reuse compatible npm dependencies (copy only)..."
        );
        npmWarmMenu.DropDownOpening += async (_, _) =>
            await PopulateNpmArtifactTransfersAsync(npmWarmMenu);
        menu.Items.Insert(10, npmWarmMenu);

        ToolStripMenuItem retainedMenu = new(
            "Open retained released workspace..."
        );
        retainedMenu.DropDownOpening += async (_, _) =>
            await PopulateRetainedReleasedAsync(retainedMenu);
        menu.Items.Insert(11, retainedMenu);

        ToolStripMenuItem interruptedMenu = new(
            "Finish interrupted checkout (verified only)..."
        );
        interruptedMenu.DropDownOpening += async (_, _) =>
            await PopulateInterruptedSlotRecoveryAsync(interruptedMenu);
        menu.Items.Insert(4, interruptedMenu);

        ToolStripItem[] items = menu.Items.Cast<ToolStripItem>().ToArray();
        menu.Items.Clear();
        ToolStripMenuItem workspaces = new("Workspaces");
        ToolStripMenuItem pool = new("Reuse and baselines");
        ToolStripMenuItem recovery = new("Snapshots and recovery");
        ToolStripMenuItem diagnostics = new("Diagnostics");
        foreach (ToolStripItem item in items)
        {
            if (item.Text.StartsWith("Open Git branch", StringComparison.Ordinal)) menu.Items.Add(item);
            else if (item.Text is "Settings..." or "Exit") continue;
            else if (item == snapshotsMenu || item == restoreMenu || item == recoveryMenu || item == interruptedMenu
                || item == branchRecovery || item == cancelReleaseMenu) recovery.DropDownItems.Add(item);
            else if (item == diskUsageMenu || item.Text.StartsWith("Local operation timings", StringComparison.Ordinal)) diagnostics.DropDownItems.Add(item);
            else if (item == parkMenu || item == unparkMenu || item == suspendMenu || item == retainedMenu) workspaces.DropDownItems.Add(item);
            else pool.DropDownItems.Add(item);
        }
        menu.Items.Add("Register repository...", null, async (_, _) => await RegisterRepositoryAsync(null));
        menu.Items.AddRange([workspaces, pool, recovery, diagnostics, new ToolStripSeparator()]);
        menu.Items.Add(items.Single(item => item.Text == "Settings..."));
        menu.Items.Add(items.Single(item => item.Text == "Exit"));
        _icon.DoubleClick += async (_, _) => await OpenGitBranchFromTrayAsync();

        _baselineTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        _baselineTimer.Tick += async (_, _) => await RefreshBaselinesAsync(false);
        _baselineTimer.Start();
        _ = ListenAsync(_shutdown.Token);
        _ = AnnounceInterruptedAllocationsAsync();

        if (initialRequest is not null)
        {
            _ = HandleRequestAsync(initialRequest);
        }
    }

    protected override void ExitThreadCore()
    {
        _baselineTimer.Stop();
        _baselineTimer.Dispose();
        _shutdown.Cancel();
        _icon.Visible = false;
        _icon.Dispose();
        _dispatcher.Dispose();
        _shutdown.Dispose();
        base.ExitThreadCore();
    }

    private async Task AnnounceInterruptedAllocationsAsync()
    {
        try
        {
            // Inform the user, but never run Git repair, checkout or cleanup
            // automatically when a previous process left a Reserved lease.
            IReadOnlyList<WorkspaceReadySlotReservation> pending =
                await _pendingSlotInventory.ListReservedAsync(_shutdown.Token);
            if (pending.Count != 0)
            {
                Notify(
                    "PR slot recovery required",
                    pending.Count + " reserved workspace slot checkout(s) need inspection. "
                        + "See Recovery diagnostics; no files were changed."
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Slot recovery inventory unavailable", exception.Message);
        }
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
                menu.DropDownItems.Add(new ToolStripMenuItem("No active workspaces")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in active)
            {
                ToolStripMenuItem item = new(
                    session.DisplayName
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
            WorkspaceSnapshotReceipt receipt = await RunOperationAsync(WorkspaceOperation.Snapshot, "Snapshot workspace",
                token => _snapshot.CaptureAsync(operationId, token));
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

            foreach (WorkspaceSnapshotRecord record in snapshots.Where(item => item.Session.BranchName is not null))
            {
                ToolStripMenuItem item = new(
                    $"{record.Session.DisplayName} "
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
                + "Existing workspaces will not be changed.",
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
            string path = await RunOperationAsync(WorkspaceOperation.Restore, "Restore snapshot", token => _restore.RestoreAsync(snapshotId, token));
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

    private async Task PopulateLocalDiskUsageAsync(
        ToolStripMenuItem menu,
        string stateRoot
    )
    {
        menu.DropDownItems.Clear();
        try
        {
            // Enumeration can be expensive. Never freeze the Windows tray UI,
            // follow reparse points, silently swallow scan errors or reclaim data.
            WorkspaceLocalStorageUsage usage = await Task.Run(
                () => new WorkspaceLocalStorageInspector(stateRoot).Inspect(_shutdown.Token),
                _shutdown.Token
            );
            menu.DropDownItems.Add(new ToolStripMenuItem(
                $"Snapshots: {usage.Snapshots.Bytes / 1048576d:F1} MiB ({usage.Snapshots.Files} files)"
            )
            {
                Enabled = false,
            });
            menu.DropDownItems.Add(new ToolStripMenuItem(
                $"Recovered: {usage.Recovered.Bytes / 1048576d:F1} MiB ({usage.Recovered.Files} files)"
            )
            {
                Enabled = false,
            });
            WorkspaceStorageWarningAssessment warning = WorkspaceStorageWarningPolicy.Assess(usage, SettingsStore.Read().StorageWarningLimits);
            menu.DropDownItems.Add(new ToolStripMenuItem(warning.AnyExceeded
                ? "Configured evidence warning threshold exceeded; review storage manually."
                : "Within configured evidence warning thresholds.") { Enabled = false });
            menu.DropDownItems.Add(new ToolStripMenuItem(
                "Read-only logical sizes; no cleanup or eviction."
            )
            {
                Enabled = false,
            });
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(
                "Disk usage unknown: " + exception.Message
            )
            {
                Enabled = false,
            });
            Notify("Storage inventory unavailable", exception.Message);
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

    private async Task PopulateInterruptedSlotRecoveryAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceReadySlotReservation> pending =
                await _pendingSlotInventory.ListReservedAsync(_shutdown.Token);
            AppSettings settings = SettingsStore.Read();
            int shown = 0;
            foreach (WorkspaceReadySlotReservation selected in pending.Take(101))
            {
                GitRepositoryRegistration? branchRepository = FindTrustedBranchRepository(selected, settings.GitRepositories);
                if (branchRepository is null)
                {
                    continue;
                }

                if (shown++ >= 100)
                {
                    break;
                }

                ToolStripMenuItem item = new(
                    selected.Session.DisplayName
                )
                {
                    ToolTipText = "Reserved generation " + selected.Lease.Generation
                        + " — " + selected.Session.WorkspacePath
                        + Environment.NewLine + "Pinned commit: "
                        + (selected.Session.PinnedCommitSha ?? "(not pinned)"),
                };
                WorkspaceReadySlotReservation choice = selected;
                item.Click += (_, _) => _ = FinishInterruptedSlotAsync(choice, branchRepository);
                menu.DropDownItems.Add(item);
            }

            if (menu.DropDownItems.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No pending checkouts for configured repositories."
                ) { Enabled = false });
            }

            if (pending.Count > 100)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "Showing at most 100 reservations; see Recovery diagnostics for more."
                ) { Enabled = false });
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Cannot list interrupted checkouts", exception.Message);
        }
    }

    private static GitRepositoryRegistration? FindTrustedBranchRepository(
        WorkspaceReadySlotReservation reservation,
        IReadOnlyList<GitRepositoryRegistration> configured
    )
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        GitRepositoryRegistration[] matches = configured.Where(repository =>
            reservation.Session.RepositoryKey.Equals(repository.RepositoryIdentity, StringComparison.Ordinal)
            && Path.GetFullPath(reservation.Session.SourcePath).Equals(Path.GetFullPath(repository.LocalPath), comparison)
            && Path.GetDirectoryName(Path.GetFullPath(reservation.Session.WorkspacePath))!
                .Equals(Path.GetFullPath(repository.WorkspaceRoot), comparison)
        ).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task FinishInterruptedSlotAsync(
        WorkspaceReadySlotReservation selected,
        GitRepositoryRegistration branchRepository
    )
    {
        DialogResult confirmed = MessageBox.Show(
            "Verify and finish the interrupted checkout for "
                + selected.Session.DisplayName + "?\n\n"
                + "Reserved slot: " + selected.Session.WorkspacePath + "\n"
                + "Generation: " + selected.Lease.Generation + "\n"
                + "Pinned commit: "
                + (selected.Session.PinnedCommitSha ?? "(missing)") + "\n\n"
                + "Only the selected unchanged reservation and a completely clean, "
                + "registered Git worktree at the pinned commit can become Active. "
                + "If any check fails, the existing reservation and files stay untouched. "
                + "No checkout, reset, cleanup or deletion will be attempted.",
            "Finish verified workspace checkout",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2
        );
        if (confirmed != DialogResult.Yes)
        {
            return;
        }

        try
        {
            WorkspaceSession recovered = await RunOperationAsync(WorkspaceOperation.RecoverSlot,
                "Verify interrupted checkout", token => _reconcileSlot.ReconcileSelectedAsync(selected, branchRepository, token));
            try
            {
                await _editor.OpenAsync(recovered.WorkspacePath, _shutdown.Token);
                Notify("Interrupted workspace checkout completed", recovered.WorkspacePath);
            }
            catch (Exception exception)
            {
                Notify("Workspace recovered, but editor did not open",
                    recovered.WorkspacePath + " — " + exception.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled verification leaves its pending lease untouched.
        }
        catch (Exception exception)
        {
            Notify("workspace checkout was not recovered",
                exception.Message + " No automatic cleanup or deletion was attempted.");
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
                menu.DropDownItems.Add(new ToolStripMenuItem("No active workspaces")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in sessions.Where(item => item.BranchName is not null))
            {
                ToolStripMenuItem item = new(
                    session.DisplayName
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
            "Suspend this workspace session? Its existing worktree and all changes will stay "
                + "exactly where they are. This will NOT free disk space or make "
                + "the slot available for reuse. Opening the workspace again resumes it.",
            "Suspend workspace session",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string path = await RunOperationAsync(WorkspaceOperation.Suspend, "Suspend workspace", token => _suspend.SuspendAsync(operationId, token));
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
                menu.DropDownItems.Add(new ToolStripMenuItem("No eligible workspaces")
                {
                    Enabled = false,
                });
                return;
            }

            foreach (WorkspaceSession session in sessions.Where(item => item.BranchName is not null))
            {
                Guid id = session.OperationId;
                ToolStripMenuItem item = new(
                    $"{session.DisplayName} ({session.State})"
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
                : "Move the complete workspace worktree into private parking under the "
                    + "same workspace root? All changes are preserved in the moved "
                    + "directory. Nothing is deleted.",
            unpark ? "Unpark workspace worktree" : "Park workspace worktree",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (choice != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string path = await RunOperationAsync(unpark ? WorkspaceOperation.Unpark : WorkspaceOperation.Park,
                unpark ? "Unpark workspace" : "Park workspace",
                token => unpark ? _parking.UnparkAsync(id, token) : _parking.ParkAsync(id, token));
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

    private async Task PopulateReleaseCandidatesAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceSession> parked =
                await _parkingJournal.ListUnparkCandidatesAsync(_shutdown.Token);
            WorkspaceSession[] eligible = parked.Where(
                session => session.State == WorkspaceSessionState.Parked
            ).ToArray();
            if (eligible.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "Park a workspace worktree first; nothing is releasable."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSession session in eligible)
            {
                ToolStripMenuItem item = new(
                    session.DisplayName
                )
                {
                    ToolTipText = session.ParkingPath ?? session.WorkspacePath,
                };
                Guid id = session.OperationId;
                item.Click += (_, _) => _ = ReleaseParkedSlotFromMenuAsync(id);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Cannot list parked workspaces", exception.Message);
        }
    }

    private async Task ReleaseParkedSlotFromMenuAsync(Guid operationId)
    {
        WorkspaceSession? session;
        try
        {
            session = await _sessions.FindAsync(operationId, _shutdown.Token);
            if (session is null || session.State != WorkspaceSessionState.Parked)
            {
                throw new InvalidOperationException(
                    "The selected workspace is no longer parked. Nothing was released."
                );
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Notify("Release unavailable", exception.Message);
            return;
        }

        DialogResult confirmed = MessageBox.Show(
            "Release the original slot of " + session.RepositoryKey
                + " [" + session.BranchName + "]" + "?\n\n"
                + "Its entire parked Git worktree, edits, staged changes and snapshots "
                + "remain at:\n" + session.ParkingPath + "\n\n"
                + "Another workspace may then use its FORMER workspace path. "
                + "You cannot unpark this workspace back into that slot. "
                + "Use the Open retained released workspace menu to edit its parked "
                + "worktree in place, without reclaiming the slot.\n\n"
                + "Continue with this irreversible ownership handoff?",
            "Release parked workspace slot",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2
        );
        if (confirmed != DialogResult.Yes)
        {
            return;
        }

        try
        {
            WorkspaceSlotReleasePreparation finished = await RunOperationAsync(
                WorkspaceOperation.ReleaseSlot, "Release parked slot", async token =>
                {
                    WorkspaceSlotReleasePreparation prepared = await _prepareRelease.PrepareAsync(operationId, token);
                    return await _finalizeRelease.FinalizeAsync(prepared.PreparationId, token);
                });
            Notify(
                "Parked workspace slot released",
                "Slot " + finished.SlotId.ToString("N")[..8]
                    + " is Ready. Parked workspace files and snapshots remain preserved."
            );
        }
        catch (OperationCanceledException)
        {
            // The prepared ownership intent remains durable for explicit inspection.
        }
        catch (Exception exception)
        {
            Notify(
                "Slot release not completed",
                exception.Message + " Check Recovery diagnostics; if a preparation "
                    + "remains, cancel it explicitly before unparking."
            );
        }
    }

    private async Task PopulateReadySlotsAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            AppSettings settings = SettingsStore.Read();
            IReadOnlyList<WorkspaceSlot> slots =
                await _readyCatalog.ListReadyAsync(_shutdown.Token);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            WorkspaceSlot[] trusted = slots.Where(slot =>
                settings.GitRepositories.Any(repository =>
                    slot.RepositoryKey.Equals(repository.RepositoryIdentity, StringComparison.Ordinal)
                    && Path.GetFullPath(repository.LocalPath).Equals(Path.GetFullPath(slot.SourcePath), comparison)
                    && Path.GetDirectoryName(Path.GetFullPath(slot.WorkspacePath))!
                        .Equals(Path.GetFullPath(repository.WorkspaceRoot), comparison)
                )
            ).ToArray();

            if (trusted.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No released slots for configured repositories."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSlot slot in trusted)
            {
                ToolStripMenuItem item = new(
                    slot.RepositoryKey + " — " + Path.GetFileName(slot.WorkspacePath)
                )
                {
                    ToolTipText = "Generation " + slot.Generation + " — "
                        + slot.WorkspacePath,
                };
                Guid slotId = slot.SlotId;
                long generation = slot.Generation;
                item.Click += (_, _) => _ = OpenBranchInSelectedSlotAsync(slot);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Cannot list released slots", exception.Message);
        }
    }

    private async Task PopulatePendingReleasesAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceSlotReleasePreparation> pending =
                await _releasePreparations.ListPreparedAsync(_shutdown.Token);
            if (pending.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No pending release preparations."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSlotReleasePreparation entry in pending)
            {
                WorkspaceSession? session = await _sessions.FindAsync(
                    entry.SessionId, _shutdown.Token
                );
                ToolStripMenuItem item = new(
                    (session is null
                        ? "Unknown PR"
                        : session.DisplayName)
                    + " — slot " + entry.SlotId.ToString("N")[..8]
                );
                Guid preparationId = entry.PreparationId;
                item.Click += (_, _) => _ = CancelReleaseFromMenuAsync(preparationId);
                menu.DropDownItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Cannot list pending releases", exception.Message);
        }
    }

    private async Task CancelReleaseFromMenuAsync(Guid preparationId)
    {
        if (MessageBox.Show(
            "Cancel this pending slot release? The original slot remains "
                + "Protected, and the workspace stays parked. No files are moved.",
            "Cancel pending release",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2
        ) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            if (!await _prepareRelease.CancelAsync(preparationId, _shutdown.Token))
            {
                throw new InvalidOperationException(
                    "The preparation was already cancelled or committed."
                );
            }

            Notify("Release preparation cancelled", "The parked workspace retains its original slot.");
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not cancel slot release", exception.Message);
        }
    }

    private async Task PopulateNpmArtifactTransfersAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceSession> active =
                await _sessions.ListActiveAsync(_shutdown.Token);
            IReadOnlyList<WorkspaceSession> allParked =
                await _recoverySessions.ListRecoveryParkingSessionsAsync(_shutdown.Token);
            StringComparison cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int added = 0;
            foreach (WorkspaceSession current in active)
            {
                // Historical released workspaces are still parked. Match exclusively
                // the old slot identity and trusted source, not similar names.
                foreach (WorkspaceSession donor in allParked.Where(x =>
                    x.State == WorkspaceSessionState.Released
                    && x.RepositoryKey.Equals(current.RepositoryKey,
                        StringComparison.OrdinalIgnoreCase)
                    && Path.GetFullPath(x.SourcePath).Equals(
                        Path.GetFullPath(current.SourcePath), cmp)
                    && Path.GetFullPath(x.WorkspacePath).Equals(
                        Path.GetFullPath(current.WorkspacePath), cmp)
                ))
                {
                    if (++added > 100)
                    {
                        throw new InvalidOperationException(
                            "Too many npm artifact donors to display safely."
                        );
                    }

                    ToolStripMenuItem item = new(
                        current.DisplayName + " ← " + donor.DisplayName
                    )
                    {
                        ToolTipText = "From " + donor.ParkingPath
                            + " to " + current.WorkspacePath,
                    };
                    Guid donorId = donor.OperationId;
                    Guid recipientId = current.OperationId;
                    item.Click += (_, _) => _ = TransferNpmArtifactsFromMenuAsync(
                        donorId, recipientId
                    );
                    menu.DropDownItems.Add(item);
                }
            }

            if (added == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No active workspace with a released donor at its workspace slot."
                ) { Enabled = false });
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Cannot inspect npm artifact sources", exception.Message);
        }
    }

    private async Task TransferNpmArtifactsFromMenuAsync(
        Guid releasedSessionId,
        Guid activeSessionId
    )
    {
        WorkspaceSession? donor;
        WorkspaceSession? target;
        try
        {
            donor = await _sessions.FindAsync(releasedSessionId, _shutdown.Token);
            target = await _sessions.FindAsync(activeSessionId, _shutdown.Token);
            if (donor?.State != WorkspaceSessionState.Released
                || target?.State != WorkspaceSessionState.Active)
            {
                throw new InvalidOperationException("The selected owners changed.");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Notify("Warm npm reuse unavailable", exception.Message);
            return;
        }

        if (MessageBox.Show(
            "Copy ignored node_modules from parked "
                + donor.DisplayName + " into " + target.DisplayName + "?\n\n"
                + "The source will remain untouched. Both package.json and "
                + "package-lock.json must match exactly. The destination "
                + "node_modules directory must not exist; links and tracked "
                + "files are refused. Up to 512 MiB / 20,000 files may be "
                + "copied. Any partial copy after an I/O failure remains "
                + "for inspection, never automatically deleted.\n\n"
                + "This is not a dependency installation or speed guarantee.",
            "Explicit npm dependency reuse",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2
        ) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            WarmArtifactTransferReceipt result = await RunOperationAsync(WorkspaceOperation.TransferArtifacts, "Copy compatible dependencies",
                token => _warmTransfer.TransferAsync(releasedSessionId, activeSessionId, token));
            Notify(
                "npm dependency cache copied",
                result.FileCount + " files / "
                    + (result.CopiedBytes / (1024 * 1024.0)).ToString("F1")
                    + " MiB in " + result.Elapsed.TotalSeconds.ToString("F2")
                    + " s. Original parked workspace unchanged."
            );
        }
        catch (OperationCanceledException)
        {
            // Any partial copy remains on disk for explicit inspection.
        }
        catch (Exception exception)
        {
            Notify(
                "npm dependency reuse refused or interrupted",
                exception.Message + " No automatic cleanup was attempted."
            );
        }
    }

    private async Task PopulateRetainedReleasedAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceSession> sessions =
                await _recoverySessions.ListRecoveryParkingSessionsAsync(_shutdown.Token);
            WorkspaceSession[] retained = sessions.Where(
                session => session.BranchName is not null && session.State == WorkspaceSessionState.Released
            ).Take(101).ToArray();

            if (retained.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No explicitly released workspaces with retained worktrees."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSession session in retained.Take(100))
            {
                ToolStripMenuItem item = new(
                    session.DisplayName
                )
                {
                    ToolTipText = session.ParkingPath ?? "Parking path missing",
                };
                Guid id = session.OperationId;
                item.Click += (_, _) => _ = OpenRetainedReleasedFromMenuAsync(id);
                menu.DropDownItems.Add(item);
            }

            if (retained.Length > 100)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "Showing first 100 workspaces; review additional records in diagnostics."
                ) { Enabled = false });
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list retained workspaces", exception.Message);
        }
    }

    private async Task OpenRetainedReleasedFromMenuAsync(Guid operationId)
    {
        try
        {
            string path = await _openRetained.OpenAsync(operationId, _shutdown.Token);
            Notify(
                "Released workspace opened from retained parking",
                "Opened " + path + ". Original slot ownership was not changed."
            );
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify(
                "Retained workspace was not opened",
                exception.Message + " No files or slot leases were changed."
            );
        }
    }

    private static void OpenSettings()
    {
        try
        {
            using WorkspaceSettingsForm settings = new();
            settings.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Could not open settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenTimings()
    {
        try
        {
            using WorkspaceTimingsForm report = new(_timings);
            report.ShowDialog();
        }
        catch (Exception exception) { Notify("Cannot read local timings", exception.Message); }
    }

    private Task<T> RunOperationAsync<T>(
        WorkspaceOperation operation,
        string title,
        Func<CancellationToken, Task<T>> action
    )
    {
        if (_dispatcher.IsDisposed) return Task.FromCanceled<T>(new CancellationToken(true));
        if (_dispatcher.InvokeRequired)
        {
            TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.BeginInvoke((Action)(async () =>
            {
                try { result.TrySetResult(await RunOperationAsync(operation, title, action)); }
                catch (OperationCanceledException) { result.TrySetCanceled(); }
                catch (Exception exception) { result.TrySetException(exception); }
            }));
            return result.Task;
        }
        return RunOnUiThreadAsync(operation, title, action);
    }

    private async Task<T> RunOnUiThreadAsync<T>(
        WorkspaceOperation operation,
        string title,
        Func<CancellationToken, Task<T>> action
    )
    {
        AppSettings settings = SettingsStore.Read();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(settings.OperationTimeoutMinutes));
        using WorkspaceOperationForm progress = new(title, cancellation);
        progress.Show();
        Stopwatch clock = Stopwatch.StartNew();
        WorkspaceOperationOutcome outcome = WorkspaceOperationOutcome.Failed;
        try
        {
            T value = await Task.Run(() => action(cancellation.Token), cancellation.Token);
            outcome = WorkspaceOperationOutcome.Completed;
            return value;
        }
        catch (OperationCanceledException)
        {
            outcome = WorkspaceOperationOutcome.Cancelled;
            Notify("Operation cancelled", "Unfinished work and recovery evidence have been preserved.");
            throw;
        }
        finally
        {
            clock.Stop();
            progress.Complete();
            if (settings.EnableLocalTimings)
            {
                try { await Task.Run(() => _timings.Record(operation, clock.Elapsed, outcome)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    Notify("Local timing was not recorded", "The operation result is unchanged. Check access to the diagnostics folder.");
                }
            }
        }
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
        if (GitBranchProtocol.TryParse(raw, out GitBranchRequest? branch))
        {
            await OpenGitBranchAsync(branch);
            return;
        }
        Notify("Rejected URL", "Use a Git repository + branch v2 link. Provider-specific workspace links are no longer supported.");
    }

    private async Task OpenGitBranchAsync(GitBranchRequest request)
    {
        try
        {
            if (!request.IsValid) throw new InvalidOperationException("Enter a valid Git clone URL and short branch name.");
            bool registered = false;
            await InvokeUiAsync(async () => { registered = await EnsureRegisteredAsync(request); });
            if (!registered) return;
            if (SettingsStore.Read().OfferVerifiedBranchReuse)
            {
                await InvokeUiAsync(() => OpenBranchWithReuseAsync(request));
            }
            else await OpenGitBranchDirectAsync(request);
        }
        catch (Exception exception) { Notify("Could not open Git branch", exception.Message); }
    }

    private async Task OpenGitBranchDirectAsync(GitBranchRequest request)
    {
        try
        {
            // SQLite inventory and filesystem preflight must not block the tray thread.
            OpenWorkspaceResult result = await RunOperationAsync(WorkspaceOperation.OpenBranch, "Open Git branch",
                token => _openBranch.HandleAsync(request, token));
            Notify(result.Reused ? "Reopened branch workspace" : "Created branch workspace", result.WorkspacePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Notify("Could not open Git branch", exception.Message); }
    }

    private async Task OpenGitBranchFromTrayAsync()
    {
        try
        {
            GitBranchRequest? request = AskForGitBranch();
            if (request is not null) await OpenGitBranchAsync(request);
        }
        catch (Exception exception)
        {
            Notify("Cannot open Git branch", exception.Message);
        }
    }

    private GitBranchRequest? AskForGitBranch(GitRepositoryRegistration? selectedRepository = null)
    {
        GitRepositoryRegistration[] configured = selectedRepository is null
            ? SettingsStore.Read().GitRepositories.ToArray() : [selectedRepository];
        using Form dialog = new()
        {
            Text = "Open Git branch", Width = 650, Height = 245,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
        };
        ComboBox repository = new() { Left = 16, Top = 42, Width = 600, DropDownStyle = ComboBoxStyle.DropDown };
        repository.Items.AddRange(configured.Select(r => r.CloneUrl).Cast<object>().ToArray());
        if (configured.Length != 0) repository.SelectedIndex = 0;
        TextBox branch = new() { Left = 16, Top = 106, Width = 600 };
        Button open = new() { Text = "Open", Left = 400, Top = 150, Width = 100, DialogResult = DialogResult.OK };
        Button cancel = new() { Text = "Cancel", Left = 510, Top = 150, Width = 100, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([
            new Label { Text = "Git clone URL (HTTPS or SSH; new URLs open registration):", Left = 16, Top = 16, Width = 600 }, repository,
            new Label { Text = "Source branch (for example feature/my-task):", Left = 16, Top = 80, Width = 600 }, branch,
            open, cancel,
        ]);
        dialog.AcceptButton = open; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK
            ? new GitBranchRequest(repository.Text.Trim(), branch.Text.Trim()) : null;
    }

    private async Task OpenBranchInSelectedSlotAsync(WorkspaceSlot slot)
    {
        try
        {
            GitRepositoryRegistration? repository = SettingsStore.Read().GitRepositories.SingleOrDefault(
                item => item.RepositoryIdentity.Equals(slot.RepositoryKey, StringComparison.Ordinal)
            );
            if (repository is null) throw new InvalidOperationException("The slot's repository is not configured.");
            GitBranchRequest? request = AskForGitBranch(repository);
            if (request is null) return;
            if (MessageBox.Show(
                "Open " + request.Branch + " in the released slot at " + slot.WorkspacePath
                    + "? The previous owner's parked files are retained. Existing branch owners reopen in place.",
                "Use released workspace slot", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            OpenWorkspaceResult result = await RunOperationAsync(WorkspaceOperation.AcquireSlot, "Open branch in released slot",
                token => _openBranch.HandleInReadySlotAsync(request, slot.SlotId, slot.Generation, token));
            Notify("Branch workspace opened", result.WorkspacePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Notify("Could not open branch in released slot", exception.Message); }
    }

    private Task InvokeUiAsync(Func<Task> action)
    {
        if (!_dispatcher.InvokeRequired) return action();
        TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke((Action)(async () =>
        {
            try { await action(); done.TrySetResult(true); }
            catch (Exception exception) { done.TrySetException(exception); }
        }));
        return done.Task;
    }

    private async Task OpenBranchWithReuseAsync(GitBranchRequest request)
    {
        try
        {
            if (!request.IsValid) throw new InvalidOperationException("Enter a valid Git repository and branch.");
            if (!await EnsureRegisteredAsync(request)) return;
            GitBranchReusePlan plan = await RunOperationAsync(WorkspaceOperation.InspectPool, "Inspect verified reuse choices",
                token => _reusePlan.PlanAsync(request, token));
            if (plan.ExistingOwner)
            {
                await OpenGitBranchDirectAsync(request);
                return;
            }
            using WorkspaceReuseChoiceForm choice = new(plan);
            DialogResult answer = choice.ShowDialog();
            if (answer == DialogResult.No)
            {
                await OpenGitBranchDirectAsync(request);
                return;
            }
            VerifiedReuseCandidate? candidate = choice.SelectedCandidate;
            if (answer != DialogResult.Yes || candidate is null) return;
            bool copyNpm = choice.CopyNpm;
            var opened = await RunOperationAsync(WorkspaceOperation.AcquireSlot, "Open selected branch workspace", async token =>
            {
                OpenWorkspaceResult result = await _openBranch.HandleInReadySlotAsync(request, candidate.SlotId,
                    candidate.Generation, token, plan.TargetCommit);
                string? warning = null;
                if (copyNpm && !result.Reused)
                {
                    try
                    {
                        GitBranchSession destination = await _branchSessions.FindAsync(request, token)
                            ?? throw new InvalidOperationException("The destination owner could not be verified.");
                        await _warmTransfer.TransferAsync(candidate.DonorSessionId, destination.OperationId, token);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        warning = "Checkout is ready, but dependency copying was refused or incomplete. "
                            + "Inspect node_modules before using cached files. " + exception.Message;
                    }
                }
                return (Result: result, Warning: warning);
            });
            Notify(opened.Warning is null ? "Branch workspace opened" : "Branch opened; inspect dependency copy",
                opened.Warning ?? opened.Result.WorkspacePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Notify("Could not open branch with reuse", exception.Message); }
    }

    private async Task RefreshBaselinesAsync(bool force)
    {
        try
        {
            AppSettings settings = SettingsStore.Read();
            _baselineSettingsErrorShown = false;
            if (!settings.GitRepositories.Any(repository => repository.BaselinePatterns is not null
                    ? repository.BaselinePatterns.Count != 0 : repository.BaselineBranches.Count != 0))
            {
                await _baselines.RefreshDueAsync([], settings.BaselineRefreshMinutes, DateTimeOffset.UtcNow, false, _shutdown.Token);
                if (force) Notify("No baselines configured", "Add exact baseline branch names in Settings. No fetch was performed.");
                return;
            }
            BaselineRefreshBatch batch = force
                ? await RunOperationAsync(WorkspaceOperation.RefreshBaseline, "Refresh configured baselines",
                    token => _baselines.RefreshDueAsync(settings.GitRepositories, settings.BaselineRefreshMinutes,
                        DateTimeOffset.UtcNow, true, token))
                : await Task.Run(() => _baselines.RefreshDueAsync(settings.GitRepositories, settings.BaselineRefreshMinutes,
                    DateTimeOffset.UtcNow, false, _shutdown.Token), _shutdown.Token);
            if (force) Notify("Baseline refresh", batch.AlreadyRunning ? "A refresh is already running."
                : batch.Attempts.Count(status => status.Succeeded) + " ref(s) refreshed; "
                    + batch.Attempts.Count(status => !status.Succeeded) + " failed. "
                    + (batch.MoreDue ? "More configured refs will be refreshed in later bounded passes." : "No worktree was changed."));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (force || !_baselineSettingsErrorShown) Notify("Baseline refresh unavailable", exception.Message);
            _baselineSettingsErrorShown = true;
        }
    }

    private void PopulateBaselineStatus(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        IReadOnlyList<BaselineRefreshStatus> statuses = _baselines.Latest();
        foreach (BaselineRefreshStatus status in statuses.Take(64))
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(status.CloneUrl + " [" + status.Branch + "]: "
                + (status.InProgress ? "refreshing..." : status.Succeeded ? (status.Commit is null ? "remote heads inspected" : status.Commit[..12]) : "fetch failed; check Git authentication/origin"))
            { Enabled = false, ToolTipText = "Last attempt: " + status.AttemptedAt.ToLocalTime().ToString("g") });
        }
        if (statuses.Count == 0) menu.DropDownItems.Add(new ToolStripMenuItem("No configured baseline has been attempted in this tray session.") { Enabled = false });
        if (statuses.Count > 64) menu.DropDownItems.Add(new ToolStripMenuItem("Showing the first 64 baseline statuses.") { Enabled = false });
    }

    private async Task<bool> EnsureRegisteredAsync(GitBranchRequest request)
    {
        if (new SettingsStore().Find(request) is not null) return true;
        return await RegisterRepositoryAsync(request.CloneUrl);
    }

    private async Task<bool> RegisterRepositoryAsync(string? cloneUrl)
    {
        try
        {
            using RepositoryRegistrationForm form = new(cloneUrl);
            if (form.ShowDialog() != DialogResult.OK || form.Registration is not GitRepositoryRegistration registration) return false;
            bool createBareClone = form.CreateBareClone;
            await RunOperationAsync(WorkspaceOperation.RegisterRepository, "Register Git repository", async token =>
            {
                GitCliWorkspaceGit git = new();
                if (createBareClone) await git.CloneBareAsync(registration.CloneUrl, registration.LocalPath, token);
                string origin = await git.GetOriginUrlAsync(registration.LocalPath, token);
                if (!GitBranchRequest.OriginMatches(origin, registration.CloneUrl)) throw new InvalidOperationException("Clone origin does not match the requested repository.");
                var (settings, fingerprint) = SettingsStore.ReadForEdit();
                if (settings.GitRepositories.Any(item => GitBranchRequest.OriginMatches(item.CloneUrl, registration.CloneUrl)))
                    throw new InvalidOperationException("Repository was registered elsewhere while this window was open. Reopen the branch.");
                settings.GitRepositories.Add(registration);
                SettingsStore.Save(settings, fingerprint);
                return true;
            });
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) { Notify("Repository was not registered", exception.Message + " Any incomplete clone remains for inspection."); return false; }
    }

    private async Task PopulateBranchRecoveryAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<GitBranchSession> sessions = await Task.Run(
                () => _branchSessions.ListUnfinishedAsync(_shutdown.Token), _shutdown.Token);
            foreach (GitBranchSession session in sessions.Take(100))
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    session.State + ": " + session.Branch + " | " + session.WorkspacePath
                    + " | commit " + (session.PinnedCommit ?? "not yet pinned")
                ) { Enabled = false });
            }
            menu.DropDownItems.Add(new ToolStripMenuItem(sessions.Count == 0
                ? "No unfinished branch workspaces."
                : "Evidence retained. Reopen retries Preparing only; RecoveryRequired needs manual inspection.") { Enabled = false });
            if (sessions.Count > 100) menu.DropDownItems.Add(new ToolStripMenuItem("Showing the first 100 records.") { Enabled = false });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Notify("Cannot inspect Git branches", exception.Message); }
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
