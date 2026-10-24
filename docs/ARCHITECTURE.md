# Architecture

## Direction of dependencies

- **Core/Domain**: PR identity, validation, and workspace/session invariants. No Git, Windows, or UI dependencies.
- **Core/Application**: narrow use cases (open a PR); depends on interfaces for Git, session journal, interprocess lock, origin verification and editor actions.
- **Infrastructure**: Git CLI, SQLite journal, file-based interprocess locks, GitHub remote identity and named-pipe transport. Provider identity checks are isolated from session state changes.
- **Tray**: local configuration, Windows protocol registration, VS Code launch and composition root. No business decisions in the user interface.
- **Userscript**: source-specific PR URL recognition and a versioned `prworkspace://open?url=...` message; never shell commands.

This is a modular monolith, not a network daemon.

## Invariants

1. Existing worktree paths are never cleaned, switched, or deleted by an open request.
2. A local repository must be explicitly configured, and its resolved Git `origin` must match the requested GitHub repository before any fetch, journal reservation or worktree creation.
3. The request URL is untrusted; no arbitrary file path, executable, ref, or command can be supplied by the browser.
4. New worktrees are created at deterministic paths beneath a configured workspace root.
5. Concurrent requests for the same workspace are serialized. PR commits use a dedicated Git ref rather than shared FETCH_HEAD.
6. There is no background compilation, dependency installation or PR prediction.
7. No attempt is made to infer workspace release from VS Code process state.

## Application boundaries

- `OpenPullRequestHandler` only parses the incoming PR identity, resolves a configured clone, invokes provider origin verification, requests workspace acquisition, and opens the editor.
- `WorkspaceAcquisitionService` is the only application service permitted to allocate or reconcile worktrees. It depends on narrow ports for Git, directories, session journal and workspace operation locks.
- `IGitRemoteReader` is separate from `IWorkspaceGit` (interface segregation); provider-specific remote verification never needs worktree operations.
- Infrastructure adapters know nothing about PR UI or tray menu behavior. The tray is the composition root.
- Future parking, snapshots and eviction must be independent application use cases, not methods added to `OpenPullRequestHandler`.

## Durable ownership for PR acquisition

- `SqliteWorkspaceSessionJournal` (infrastructure) records a unique PR/worktree owner and append-only transition events under `%LOCALAPPDATA%\\GitWorkspacePool\\sessions.db`. The database uses a direct ADO.NET adapter rather than a generic repository.
- `FileWorkspaceOperationLock` holds a `FileShare.None` lock on a per-workspace file. It works across process boundaries, and the OS releases it on process termination. A single in-process semaphore alone is insufficient.
- Before checkout: persist `Preparing`, fetch the PR, resolve its **full commit SHA**, persist the SHA, then create a detached worktree at that exact commit. Never use moving `FETCH_HEAD` for recovery.
- After checkout: verify both filesystem presence and Git worktree registration, then compare the checked-out HEAD to the pinned SHA before marking `Active`.
- On retry after interruption: a matching pinned/registered worktree can be reactivated without checkout; a missing worktree may resume from the pinned commit; conflicting paths are marked `RecoveryRequired` without modifying files.
- Once `Active`, a PR worktree is always reopened **unchanged**. The user may commit or change branches. Missing active worktrees are quarantined, never silently reallocated.
- A previous preview worktree without an ownership journal is deliberately **not silently adopted**; a future explicit adoption/migration flow is required.

The journal is an ownership protocol, not a transactional filesystem. No automatic deletion, renaming, parking, artifact reuse, or cross-PR reassignment exists yet. `RecoveryRequired` means stop and inspect; it never grants permission to clean.

## Non-destructive workspace snapshot boundary

- `CaptureWorkspaceSnapshotService` is independent of `OpenPullRequestHandler` and `WorkspaceAcquisitionService`.
- An explicit, active session ID is checked against SQLite, then locked and rechecked. Capture verifies worktree registration without changing HEAD, index or working files.
- Git status is collected using NUL-delimited porcelain, with ignored and untracked files included. The linked Git index and staged blobs are copied separately from working files.
- Snapshot payloads are content-hashed and rechecked against the source. A versioned manifest records files, missing paths, original HEAD and status.
- Snapshot storage refuses symlink/junction paths and mixed snapshot/worktree ancestors. Each operation stages under `.pending-*`, publishes with a directory rename, then records the receipt in SQLite.
- The exporter deliberately rejects snapshots exceeding a conservative file/byte budget. No automatic cleanup of incomplete snapshots.
- `RestoreWorkspaceSnapshotService` accepts only receipts that were recorded in SQLite and retrieves their original owner. `VerifiedWorkspaceSnapshotRestorer` validates all listed payloads and hashes **before** invoking `git worktree add`.
- Restore builds a new isolated linked worktree at the captured immutable commit. It reapplies working files and recorded deletions, reinstates the saved Git index and verifies the exact porcelain status against the original. The active PR path and ownership record remain unchanged.
- Restoration output is outside the warm pool (`%LOCALAPPDATA%\\GitWorkspacePool\\recovered`), and orphaned or interrupted recovery worktrees are never automatically deleted.
- **Restoration is now verified on offline E2E fixtures**, but explicit release, Git worktree moves, deletion, snapshot pruning and warm-pool recycling remain disabled pending fault-injection and recovery tests.

## Deterministic snapshot interruption tests

- `ISnapshotCheckpointSink` is an infrastructure-only, optional test seam. Production does not instantiate a sink and no external or PR-controlled input can select a checkpoint.
- Export checkpoints follow copied index, verified pre-publication manifest, and published directory **before** the SQLite receipt. A failure never deletes the source. Pending and published-but-unjournalled snapshot directories remain available for inspection.
- Restore checkpoints follow registered recovery worktree creation, working-file application, and Git index verification. Injected exceptions leave a separate Git worktree that must not be auto-cleaned.
- Offline E2E covers retries, persistent ownership, and preservation of failed-operation output. A dedicated `GitWorkspacePool.CrashProbe` executable can block at a checkpoint while the test runner **kills the actual child process**. Tests verify Windows releases its interprocess locks and preserves orphaned worktrees and user data.
- Coverage so far includes kill-before-publication, kill-after-publication-before-receipt, and recovery-worktree creation/index checkpoints. **Power loss, low disk and concurrent mutation remain separate RC gates.**

## Read-only recovery inventory

- `IWorkspaceRecoveryInspector` reports incomplete exports, snapshots missing an SQLite receipt, journalled snapshots missing their directory, and isolated recovery worktrees.
- The infrastructure inspector uses `IWorkspaceRecoveryJournal` to read **all** snapshot receipts, not the UI's last 50; scans are bounded and never change ownership, Git state or filesystem contents.
- Junctions/symlinks are flagged but not traversed. A recovered worktree is verified against its original clone's Git registration where possible.
- `Recovery diagnostics...` in the tray shows findings for manual inspection only. Do not automatically delete orphaned data, including after a reboot.

## Non-destructive session suspension

- `SuspendWorkspaceSessionService` validates the active owner under the same cross-process worktree lock used by acquisition. It marks the session `Suspended` in SQLite without running Git checkout, touching files or releasing exclusive PR ownership.
- Opening the same PR revalidates the registered worktree and resumes the journalled session. Staged, unstaged, ignored and untracked files remain intact.
- `RecoveryRequired` is now an absolute quarantine barrier. No opening/retry may reactivate it automatically even when the observed HEAD matches the old pinned commit.
- SQLite v4 expands the session state constraint through a transactional table rebuild while retaining session, snapshot, restore and audit foreign keys. Explicit version migration is tested from a v3 database.
- Suspension is not parking or release: disk usage remains unchanged and no worktree is eligible for reuse.

## Durable non-destructive parking

- `ParkWorkspaceSessionService` owns the explicit Park/Unpark state machine; `IWorkspaceParkingJournal` owns SQLite state, while `IWorkspaceParkingOperations` wraps Git/Windows filesystem operations. No coupling to tray UI.
- `Active/Suspended → Parking → Parked → Unparking → Active` transitions are journalled with deterministic reserved paths and pinned pre-move HEAD. `RecoveryRequired` blocks reopening.
- Both moves use `git worktree move` (not raw `Directory.Move`) to preserve Git administrative registration and all working files. Parking remains on the same volume beneath a dedicated directory.
- An interrupted operation reconciles original vs parked Git registrations before retry/commit. Occupied paths, symlinks/junctions, conflicting registrations and failed verification never trigger deletion.
- SQLite schema v5 adds owner-bound parking path and move identity; v3/v4 upgrade retains snapshot and restore foreign-key references.
- SQLite reservation and parking intents are serialized within one transaction and reject path equality and ancestor/descendant overlaps across all PR worktrees, other parked paths, and the session's own worktree. A parked tree can never be claimed as another PR workspace simply because its original path is empty.
- **This is not a release or pool eviction.** Automatic reuse is disabled until stronger process-kill, concurrency, submodule and Windows sharing-violation coverage passes.

## Subsequent slices

- Durable session journal and safe parking/snapshot protocol.
- Warm worktree pool with explicit ownership and recovery.
- Artifact reuse policies and optional ReFS block cloning.
- Additional Git provider adapters.
- Disk-aware eviction and baseline-branch refresh.

No automatic reclamation of user data until snapshots and crash recovery have dedicated integration tests.

## Generic Git branches and shared ownership (schema v9)

`GitBranchProtocol` accepts only a configured HTTPS clone URL and short branch name. `OpenGitBranchHandler` verifies the resolved origin, fetches an ordinary branch into an operation ref, persists its immutable commit, and preserves active edits on reopen. No PR API or fabricated PR URL is involved.

The shared journal's historical `pr_number` column is a positive reference discriminator. GitHub references retain their real PR number; ordinary Git branches get a durable internal discriminator in `git_branch_subjects`. A canonical clone-URL hash forms the branch repository identity, and the branch name uses binary case-sensitive matching. Neither internal number nor hash is presented as a PR. Shared session reads carry clone/branch metadata for display.

The v9 additive upgrade imports legacy generic-branch records into `sessions` and protected slot leases within one transaction. It preserves original operation IDs, paths, state and pinned commits, retains the old table as evidence, and does not move or adopt filesystem contents. Conflicting ownership aborts the transaction. Existing snapshot, restore, event and lease foreign keys remain intact. Older binaries reject the new schema.

Branch snapshot/parking/release/restore uses the same application services as PR sessions. Explicit branch slot checkout and PR slot checkout share the same reservation, pin, verification and generation-CAS implementation; only remote head fetching differs. A released branch reopens its retained parked worktree without touching the former slot. Ordinary open never activates a Reserved generation.
