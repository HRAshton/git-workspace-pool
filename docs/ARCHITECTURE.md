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

## Durable ownership for PR acquisition

- `SqliteWorkspaceSessionJournal` (infrastructure) records a unique PR/worktree owner and append-only transition events under `%LOCALAPPDATA%\\GitWorkspacePool\\sessions.db`. The database uses a direct ADO.NET adapter rather than a generic repository.
- `FileWorkspaceOperationLock` holds a `FileShare.None` lock on a per-workspace file. It works across process boundaries, and the OS releases it on process termination. A single in-process semaphore alone is insufficient.
- Before checkout: persist `Preparing`, fetch the PR, resolve its **full commit SHA**, persist the SHA, then create a detached worktree at that exact commit. Never use moving `FETCH_HEAD` for recovery.
- After checkout: verify both filesystem presence and Git worktree registration, then compare the checked-out HEAD to the pinned SHA before marking `Active`.
- On retry after interruption: a matching pinned/registered worktree can be reactivated without checkout; a missing worktree may resume from the pinned commit; conflicting paths are marked `RecoveryRequired` without modifying files.
- Once `Active`, a PR worktree is always reopened **unchanged**. The user may commit or change branches. Missing active worktrees are quarantined, never silently reallocated.
- A previous preview worktree without an ownership journal is deliberately **not silently adopted**; a future explicit adoption/migration flow is required.

The journal is an ownership protocol, not a transactional filesystem. No automatic deletion, renaming, parking, artifact reuse, or cross-PR reassignment exists yet. `RecoveryRequired` means stop and inspect; it never grants permission to clean.

## Subsequent slices

- Durable session journal and safe parking/snapshot protocol.
- Warm worktree pool with explicit ownership and recovery.
- Artifact reuse policies and optional ReFS block cloning.
- Additional Git provider adapters.
- Disk-aware eviction and baseline-branch refresh.

No automatic reclamation of user data until snapshots and crash recovery have dedicated integration tests.
