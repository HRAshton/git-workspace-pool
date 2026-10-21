# Architecture

## Direction of dependencies

- **Core/Domain**: PR identity, validation, and workspace/session invariants. No Git, Windows, or UI dependencies.
- **Core/Application**: narrow use cases (open a PR); depends on interfaces for Git and editor actions.
- **Tray/Infrastructure**: Git CLI, local configuration, Windows protocol registration, named-pipe transport, VS Code launch. No business decisions in the user interface.
- **Userscript**: source-specific PR URL recognition and a versioned `prworkspace://open?url=...` message; never shell commands.

This is a modular monolith, not a network daemon.

## Invariants

1. Existing worktree paths are never cleaned, switched, or deleted by an open request.
2. A local repository must be explicitly configured before any fetch or worktree creation.
3. The request URL is untrusted; no arbitrary file path, executable, ref, or command can be supplied by the browser.
4. New worktrees are created at deterministic paths beneath a configured workspace root.
5. Concurrent requests for the same workspace are serialized.
6. There is no background compilation, dependency installation or PR prediction.
7. No attempt is made to infer workspace release from VS Code process state.

## Subsequent slices

- Durable session journal and safe parking/snapshot protocol.
- Warm worktree pool with explicit ownership and recovery.
- Artifact reuse policies and optional ReFS block cloning.
- Additional Git provider adapters.
- Disk-aware eviction and baseline-branch refresh.

No automatic reclamation of user data until snapshots and crash recovery have dedicated integration tests.
