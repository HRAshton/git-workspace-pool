using System.Drawing;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
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
    private readonly OpenPullRequestInReadySlotHandler _openInReadySlot;
    private readonly ReconcileExplicitReadySlotService _reconcileSlot;
    private readonly IEditorLauncher _editor;

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
        WorkspaceAcquisitionService acquisition = new(
            git,
            new WorkspaceDirectories(),
            journal,
            locks
        );
        SettingsStore repositories = new();
        GitHubOriginVerifier originVerifier = new(git);
        VsCodeLauncher editor = new();
        _editor = editor;
        _reconcileSlot = new ReconcileExplicitReadySlotService(
            slots, slots, new WorkspaceReadySlotSafety(),
            new VerifiedReadySlotCheckout(), git, locks
        );
        _openRetained = new OpenReleasedParkedWorkspaceService(
            journal, locks, new GitWorkspaceParkingOperations(git), editor
        );
        _open = new OpenPullRequestHandler(
            repositories, originVerifier, acquisition, editor, journal, _openRetained
        );
        _openInReadySlot = new OpenPullRequestInReadySlotHandler(
            repositories,
            originVerifier,
            new AcquireExplicitReadySlotService(
                slots, slots, new WorkspaceReadySlotSafety(), git,
                new WorkspaceDirectories(), journal, locks
            ),
            editor
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

        ToolStripMenuItem releaseMenu = new("Release parked PR slot (keep user files)...");
        releaseMenu.DropDownOpening += async (_, _) =>
            await PopulateReleaseCandidatesAsync(releaseMenu);
        menu.Items.Insert(7, releaseMenu);

        ToolStripMenuItem readyMenu = new("Open PR in a released slot...");
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
            "Open retained released PR (without moving files)..."
        );
        retainedMenu.DropDownOpening += async (_, _) =>
            await PopulateRetainedReleasedAsync(retainedMenu);
        menu.Items.Insert(11, retainedMenu);

        ToolStripMenuItem interruptedMenu = new(
            "Finish interrupted PR checkout (verified only)..."
        );
        interruptedMenu.DropDownOpening += async (_, _) =>
            await PopulateInterruptedSlotRecoveryAsync(interruptedMenu);
        menu.Items.Insert(4, interruptedMenu);

        _ = ListenAsync(_shutdown.Token);
        _ = AnnounceInterruptedAllocationsAsync();

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
                    pending.Count + " reserved PR slot checkout(s) need inspection. "
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

    private async Task PopulateInterruptedSlotRecoveryAsync(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            IReadOnlyList<WorkspaceReadySlotReservation> pending =
                await _pendingSlotInventory.ListReservedAsync(_shutdown.Token);
            IReadOnlyList<RepositoryRegistration> configured = SettingsStore.Read().Repositories;
            int shown = 0;
            foreach (WorkspaceReadySlotReservation selected in pending.Take(101))
            {
                RepositoryRegistration? trusted =
                    FindTrustedRecoveryRepository(selected, configured);
                if (trusted is null)
                {
                    continue;
                }

                if (shown++ >= 100)
                {
                    break;
                }

                ToolStripMenuItem item = new(
                    selected.Session.RepositoryKey + " PR #"
                        + selected.Session.PullRequestNumber
                )
                {
                    ToolTipText = "Reserved generation " + selected.Lease.Generation
                        + " — " + selected.Session.WorkspacePath
                        + Environment.NewLine + "Pinned commit: "
                        + (selected.Session.PinnedCommitSha ?? "(not pinned)"),
                };
                WorkspaceReadySlotReservation choice = selected;
                RepositoryRegistration repository = trusted;
                item.Click += (_, _) => _ = FinishInterruptedSlotAsync(choice, repository);
                menu.DropDownItems.Add(item);
            }

            if (menu.DropDownItems.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No pending PR checkouts for configured repositories."
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

    private static RepositoryRegistration? FindTrustedRecoveryRepository(
        WorkspaceReadySlotReservation reservation,
        IReadOnlyList<RepositoryRegistration> configured
    )
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        RepositoryRegistration[] matches = configured.Where(repository =>
            Path.IsPathFullyQualified(repository.LocalPath)
            && Path.IsPathFullyQualified(repository.WorkspaceRoot)
            && Directory.Exists(repository.LocalPath)
            && reservation.Session.RepositoryKey.Equals(
                "github:" + repository.RepositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
            && Path.GetFullPath(reservation.Session.SourcePath).Equals(
                Path.GetFullPath(repository.LocalPath), comparison
            )
            && Path.GetDirectoryName(Path.GetFullPath(reservation.Session.WorkspacePath))!
                .Equals(Path.GetFullPath(repository.WorkspaceRoot), comparison)
        ).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task FinishInterruptedSlotAsync(
        WorkspaceReadySlotReservation selected,
        RepositoryRegistration trusted
    )
    {
        DialogResult confirmed = MessageBox.Show(
            "Verify and finish the interrupted checkout for "
                + selected.Session.RepositoryKey + " PR #"
                + selected.Session.PullRequestNumber + "?\n\n"
                + "Reserved slot: " + selected.Session.WorkspacePath + "\n"
                + "Generation: " + selected.Lease.Generation + "\n"
                + "Pinned commit: "
                + (selected.Session.PinnedCommitSha ?? "(missing)") + "\n\n"
                + "Only the selected unchanged reservation and a completely clean, "
                + "registered Git worktree at the pinned commit can become Active. "
                + "If any check fails, the existing reservation and files stay untouched. "
                + "No checkout, reset, cleanup or deletion will be attempted.",
            "Finish verified PR checkout",
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
            WorkspaceSession recovered = await _reconcileSlot.ReconcileSelectedAsync(
                selected, trusted, _shutdown.Token
            );
            try
            {
                await _editor.OpenAsync(recovered.WorkspacePath, _shutdown.Token);
                Notify("Interrupted PR checkout completed", recovered.WorkspacePath);
            }
            catch (Exception exception)
            {
                Notify("PR recovered, but editor did not open",
                    recovered.WorkspacePath + " — " + exception.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled verification leaves its pending lease untouched.
        }
        catch (Exception exception)
        {
            Notify("PR checkout was not recovered",
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
                    "Park a PR worktree first; nothing is releasable."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSession session in eligible)
            {
                ToolStripMenuItem item = new(
                    session.RepositoryKey + " PR #" + session.PullRequestNumber
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
            Notify("Cannot list parked PRs", exception.Message);
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
                    "The selected PR is no longer parked. Nothing was released."
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
                + " PR #" + session.PullRequestNumber + "?\n\n"
                + "Its entire parked Git worktree, edits, staged changes and snapshots "
                + "remain at:\n" + session.ParkingPath + "\n\n"
                + "Another PR may then use its FORMER workspace path. "
                + "You cannot unpark this PR back into that slot. "
                + "Use the Open retained released PR menu to edit its parked "
                + "worktree in place, without reclaiming the slot.\n\n"
                + "Continue with this irreversible ownership handoff?",
            "Release parked PR slot",
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
            WorkspaceSlotReleasePreparation prepared =
                await _prepareRelease.PrepareAsync(operationId, _shutdown.Token);
            WorkspaceSlotReleasePreparation finished =
                await _finalizeRelease.FinalizeAsync(
                    prepared.PreparationId, _shutdown.Token
                );
            Notify(
                "Parked PR slot released",
                "Slot " + finished.SlotId.ToString("N")[..8]
                    + " is Ready. Parked PR files and snapshots remain preserved."
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
                settings.Repositories.Any(repository =>
                    slot.RepositoryKey.Equals(
                        "github:" + repository.RepositoryKey,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && Path.GetFullPath(repository.LocalPath).Equals(
                        Path.GetFullPath(slot.SourcePath), comparison
                    )
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
                item.Click += (_, _) => _ = OpenInSelectedSlotAsync(
                    slotId, generation
                );
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

    private async Task OpenInSelectedSlotAsync(Guid slotId, long generation)
    {
        string? url = AskForPullRequestUrl();
        if (url is null)
        {
            return;
        }

        if (!PullRequestReference.TryParse(url, out PullRequestReference? request)
            || request is null)
        {
            Notify("Invalid pull request URL", "Enter a supported GitHub HTTPS PR URL.");
            return;
        }

        WorkspaceSlot? slot;
        try
        {
            // The selection is advisory; the service verifies state, generation,
            // source, Git registration and path safety again under the OS lock.
            slot = await _slotRegistry.FindSlotAsync(
                slotId, _shutdown.Token
            );
            if (slot is null || slot.State != WorkspaceSlotState.Ready
                || slot.Generation != generation
                || !slot.RepositoryKey.Equals(
                    request.Provider + ":" + request.RepositoryKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The selected slot is stale or belongs to a different repository."
                );
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Notify("Cannot use selected slot", exception.Message);
            return;
        }

        DialogResult confirmed = MessageBox.Show(
            "Open " + request.RepositoryKey + " PR #" + request.Number
                + " in this explicitly released slot?\n\n"
                + slot.WorkspacePath + "\n\n"
                + "The previous PR remains parked elsewhere. This action reserves "
                + "a new exclusive lease and creates a fresh Git checkout. "
                + "It does not reuse build artifacts or delete files. "
                + "If interrupted, manual recovery may be required.",
            "Use released PR slot",
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
            OpenPullRequestResult result = await _openInReadySlot.HandleAsync(
                url, slotId, generation, _shutdown.Token
            );
            Notify("PR opened in released slot", result.WorkspacePath);
        }
        catch (OperationCanceledException)
        {
            // An interrupted reservation stays durable, never automatically freed.
        }
        catch (Exception exception)
        {
            Notify(
                "PR slot allocation not completed",
                exception.Message + " Recovery diagnostics will preserve any "
                    + "unfinished allocation; no automatic cleanup is performed."
            );
        }
    }

    private static string? AskForPullRequestUrl()
    {
        using Form dialog = new()
        {
            Text = "Open PR in released slot",
            Width = 580,
            Height = 185,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        Label prompt = new()
        {
            Text = "GitHub pull request URL (HTTPS):",
            Left = 16,
            Top = 16,
            Width = 520,
            Height = 22,
        };
        TextBox input = new()
        {
            Left = 16,
            Top = 42,
            Width = 530,
        };
        Button open = new()
        {
            Text = "Continue",
            Left = 340,
            Top = 86,
            Width = 100,
            DialogResult = DialogResult.OK,
        };
        Button cancel = new()
        {
            Text = "Cancel",
            Left = 450,
            Top = 86,
            Width = 95,
            DialogResult = DialogResult.Cancel,
        };
        dialog.Controls.AddRange([prompt, input, open, cancel]);
        dialog.AcceptButton = open;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
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
                        : session.RepositoryKey + " PR #" + session.PullRequestNumber)
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
                + "Protected, and the PR stays parked. No files are moved.",
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

            Notify("Release preparation cancelled", "The parked PR retains its original slot.");
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
                // Historical released PRs are still parked. Match exclusively
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
                        current.RepositoryKey + " PR #" + current.PullRequestNumber
                        + " ← parked PR #" + donor.PullRequestNumber
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
                    "No active PR with a released donor at its workspace slot."
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
                + donor.RepositoryKey + " PR #" + donor.PullRequestNumber
                + " into active PR #" + target.PullRequestNumber + "?\n\n"
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
            WarmArtifactTransferReceipt result = await _warmTransfer.TransferAsync(
                releasedSessionId, activeSessionId, _shutdown.Token
            );
            Notify(
                "npm dependency cache copied",
                result.FileCount + " files / "
                    + (result.CopiedBytes / (1024 * 1024.0)).ToString("F1")
                    + " MiB in " + result.Elapsed.TotalSeconds.ToString("F2")
                    + " s. Original parked PR unchanged."
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
                session => session.State == WorkspaceSessionState.Released
            ).Take(101).ToArray();

            if (retained.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem(
                    "No explicitly released PRs with retained worktrees."
                ) { Enabled = false });
                return;
            }

            foreach (WorkspaceSession session in retained.Take(100))
            {
                ToolStripMenuItem item = new(
                    session.RepositoryKey + " PR #" + session.PullRequestNumber
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
                    "Showing first 100 PRs; review additional records in diagnostics."
                ) { Enabled = false });
            }
        }
        catch (OperationCanceledException)
        {
            // Application is exiting.
        }
        catch (Exception exception)
        {
            Notify("Could not list retained PRs", exception.Message);
        }
    }

    private async Task OpenRetainedReleasedFromMenuAsync(Guid operationId)
    {
        try
        {
            string path = await _openRetained.OpenAsync(operationId, _shutdown.Token);
            Notify(
                "Released PR opened from retained parking",
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
                "Retained PR was not opened",
                exception.Message + " No files or slot leases were changed."
            );
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
