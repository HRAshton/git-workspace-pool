# Git Workspace Pool

[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/HRAshton/git-workspace-pool/badge)](https://scorecard.dev/viewer/?uri=github.com/HRAshton/git-workspace-pool)
[![REUSE lint](https://github.com/HRAshton/git-workspace-pool/actions/workflows/reuse.yml/badge.svg)](https://github.com/HRAshton/git-workspace-pool/actions/workflows/reuse.yml)

A Windows tray application that opens a trusted Git repository and branch in VS Code from a browser link:

```text
prworkspace://open/?repo=https%3A%2F%2Fexample.com%2Fteam%2Frepo.git&branch=feature%2Ftask
```

The same C#/.NET WinForms application now manages populated worktrees directly. Existing assignments reopen unchanged, including local commits, staged changes and untracked files. New branches prefer a safe available worktree and switch it in place. Ignored dependencies and build outputs remain in that directory. If no donor is safe, the application creates a fresh detached worktree at a new path.

## Getting started

1. Build the existing solution with the SDK in `global.json` on Windows: `dotnet build GitWorkspacePool.slnx -c Release`.
2. Start the tray and open **Settings**. Register an existing normal/bare source clone or explicitly create a new bare clone. Use a separate workspace root. Authentication uses your Git credential helper or SSH agent and existing known_hosts.
3. Choose **Prepare warm workspace**, select the repository and enter a literal baseline branch such as `main`. The operation creates an Available checkout. Install dependencies manually in that directory if desired. Keep tracked files clean and user files ignored by an intentional repository policy.
4. Use **Open branch** or install the [userscript](userscript/git-branch-workspace.user.js) for supported browser pages. On a first link for an unknown repository, review the repository URL and approve registration. The app can clone a new bare source with visible progress or use an existing trusted source clone. A browser link cannot grant trust without confirmation.
5. Use **List workspaces / release** to explicitly return an assignment to Available. Release refuses changed HEADs, local branches, tracked changes, untracked files and unfinished Git operations. No cleanup or parking occurs.

Settings remain in `%APPDATA%/GitWorkspacePool/settings.json`. New state is `%LOCALAPPDATA%/GitWorkspacePool/warm-v1/workspaces.db`. Existing `sessions.db`, snapshots and old worktrees are never migrated or adopted. Read [preservation and validation evidence](docs/REWRITE_EVIDENCE.md) before using an existing development profile.

## Safety and supported scope

Ownership states are Available, Assigned, Transitioning and Quarantined. A repository-wide interprocess lock covers selection through publication. Intent is committed before Git changes anything; checkout failures retain the entire directory and quarantine its ownership. A later operation quarantines abandoned intents under that same lock. Diagnostics never repairs, deletes or retries them.

Detached HEAD is intentional. Local commits are protected: existing owners reopen without checkout or fetch, and explicit release requires the original recorded HEAD. Editor launch failures retain a valid assignment. Git calls are bounded and noninteractive, use argument lists, disable hooks/FSMonitor and reject executable filters. Sparse/partial clones, worktree-specific configuration, submodules and tracked symlinks are unsupported for managed checkout and fail closed. Ignored cache links are never traversed or copied; links on incoming tracked paths or managed directory ancestors are rejected.

Optional `ArtifactInputs` per registration accepts **literal repository-relative file paths**, e.g. `["package-lock.json", "Directory.Packages.props"]`. Cached and target input blob fingerprints must match for warm reuse. Missing inputs reject reuse. Without a policy, arbitrary ignored outputs have unknown validity; the build tool remains responsible for staleness. The application never builds, restores, deletes caches, copies artifacts, polls remotes or discovers branches by regex.

## Validation

`scripts/Test-Changes.ps1` builds once and runs the test suite once. PR CI adds a self-contained Windows package and isolated installer smoke test. Windows build/test CI runs on PRs; REUSE lint runs on PRs and main, while Scorecard runs on main and weekly. GitHub-managed CodeQL default setup performs code scanning. Renovate is configured for grouped Monday dependency updates with non-major automerge after checks pass (requires the Renovate GitHub App). Candidate attestation is an explicit manual distribution operation.

Tests cover real disposable Git transitions, assigned dirty/local-commit reopen, artifact collisions, ranking, durable SQLite ownership, concurrent locks, failure quarantine and killed application processes at intent/checkout publication boundaries. A synthetic 10,000-file benchmark reports median/p95 fresh, warm and reopen workspace-ready timings plus tracked-file timestamp changes and retained artifact bytes. Its artifact is evidence for that fixture only. **Full application timing on representative Windows repositories and installed browser-to-visible-editor Windows acceptance remain outstanding.** No real-world speedup is claimed.

The previous snapshot/parking implementation is preserved on [`archive/pre-in-place-rewrite`](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite). Historical documentation under [docs/archive](docs/archive) applies only to that version. See [architecture](docs/ARCHITECTURE.md), [recovery](docs/RECOVERY.md) and [roadmap](ROADMAP.md).

## License and supply-chain status

First-party source and documentation are licensed under the [Zero-Clause BSD License (0BSD)](LICENSE). File-level licensing is documented in [REUSE.toml](REUSE.toml) and [LICENSES/0BSD.txt](LICENSES/0BSD.txt), with an automated REUSE check. Third-party dependencies keep their own licenses.

The dynamic [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/HRAshton/git-workspace-pool) is published after a Scorecard workflow run. See [prerelease security checklist](docs/SECURITY_SCORECARD.md) for controls requiring maintainer settings and [security reporting](SECURITY.md). A badge does not certify a release.
