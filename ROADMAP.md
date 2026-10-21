# Roadmap

**Full gated implementation plan through RC1:** [Release Candidate Plan](docs/RELEASE_CANDIDATE_PLAN.md). This roadmap tracks current implementation status; the RC plan defines complete scope, dependencies, test strategy and release gates.

> Goal: minimize time from opening a pull request to editing it locally, with minimum reasonable disk overhead and no loss of user changes.

This is a Windows tray app + Tampermonkey userscript. The application **never** builds projects, restores dependencies, runs tests from a PR, or predicts which PR will be opened next.

## Delivery rules

- Each phase ships separately with a measured acceptance gate.
- No destructive cleanup or worktree recycling until crash recovery and snapshot tests are present.
- Reopen an already-owned PR workspace before considering any other candidate.
- A workspace is never implicitly released by closing VS Code.
- Keep Git provider parsing outside Git operations; add providers behind explicit adapters.
- Match coding conventions in [WinCfApiWrapper](https://github.com/HRAshton/WinCfApiWrapper): CSharpier, StyleCop, nullable C#, file-scoped namespaces.
- Every state-changing operation must be cancellable (where safe), testable, and recoverable.

## P0 — Safe open path and fast tests

- [x] Browser button for GitHub PRs; `prworkspace://` protocol; Windows tray app.
- [x] Explicit repository configuration and create/reopen without touching existing worktrees.
- [x] Git CLI adapter and VS Code launcher behind application interfaces.
- [x] Unit tests and Windows CI.
- [x] **Fast local Git E2E:** temporary bare remote + local clone + fake `refs/pull/N/head`; real `git fetch` and `git worktree add`; fake editor; no network, browser or UI automation.
- [x] **Transport E2E:** versioned protocol URL validation, real user-scoped named-pipe transport, and URL → pipe → Git handler → fake editor in an offline fixture.
- [ ] **Single-instance process E2E:** spawn second protocol launcher, verify mutex forwarding into the existing tray instance, and verify error handling.
- [x] Recover a pinned checkout after interruption; quarantine mismatching/unknown paths without cleanup.
- [x] Verify the configured GitHub clone's resolved `origin` (HTTPS or SSH) before allocating; reject lookalike hosts and mismatching repositories.
- [ ] Extend remote identity validation to each newly supported Git provider.
- [x] Prevent concurrent or cross-process duplicate allocations via SQLite unique ownership and OS file locks.

**Gate:** tests cover create, reopen, unchanged local edits, reject unknown folders, invalid URLs, concurrent clicks, and interrupted Git operations. No network dependency in the fast lane.

## P1 — Session ownership and snapshots

- [x] Durable operation journal for PR acquisition: reservation, immutable commit pin, verification, activation, quarantine.
- [x] Extend the journal to record published snapshot receipts and audit events (SQLite schema v2).
- [ ] Extend the journal to session release, moves, parking, and eviction.
- [x] Minimal state machine for acquisition: preparing / active / recovery-required.
- [x] Add an explicit non-destructive suspended state; reopen resumes the exact owned worktree without deleting or moving files.
- [x] Test SQLite v3 → v4 schema upgrade preserving existing session, snapshot and restore foreign-key references.
- [x] Forbid automatic reactivation of quarantined `RecoveryRequired` sessions.
- [ ] Extend state machine to released / warm / parked with safe ownership transfers.
- [ ] Explicit release only. No release inferred from editor lifecycle.
- [x] Implement explicit non-destructive snapshot export for staged/unstaged, untracked, ignored and binary files, with independent staged blobs and a Git index.
- [x] Restore a journalled snapshot into a separate Git worktree on the saved commit; verify index, working tree files and exact Git status.
- [x] Reject corrupted payloads and manifest path traversal before allocating a recovery worktree.
- [x] Discover interrupted exports, published-but-unjournalled snapshots, missing receipts and unmanaged recovery worktrees without deleting data; expose findings in tray diagnostics.
- [x] Persist restore intent before worktree allocation, verify after reconstruction, and distinguish interrupted/verified/missing recovery outcomes.
- [x] Reject snapshot/restore before allocation when the target disk fails a conservative free-space preflight.
- [x] Reject executable Git filter drivers before worktree checkout; disable Git LFS smudge/process as a safe identity fallback.
- [x] Restore snapshot directory attributes and timestamps after materializing child files.
- [ ] Complete reparse-point race, actual disk-full and safe restore-reconciliation tests before enabling release.
- [ ] Configurable artifact retention and cleanup policies.
- [x] Tests for interrupted worktree creation (before journal commit) and cross-handler allocation.
- [x] Refuse snapshots of locked files without modifying the source or publishing a partial copy.
- [x] Deterministic failpoints for snapshot export and side-by-side restore; confirm no source changes, orphan retention and successful retry.
- [ ] Add fault injection across release, move, park and eviction when those use cases exist. See M1/M2 in the [RC plan](docs/RELEASE_CANDIDATE_PLAN.md).

**Gate:** every destructive transition can recover after simulated kill; unknown/user data is never silently removed.

## P2 — Reusable warm pool

- [ ] Select a free worktree using cheap Git metadata and estimated preparation cost.
- [ ] Preserve configured artifacts such as `node_modules`, `bin`, `obj`; no build execution.
- [ ] Safe fallback for unsupported paths/filesystems.
- [ ] Optional ReFS block cloning where available.
- [ ] Disk budget and eviction based on size, usage, and recreation cost.
- [ ] Explicitly configured baseline branches (`main`, `master`, `release/*`) refreshed in the background, without checkout.

**Gate:** measured reduction in time-to-editor against cold checkout on representative large-file fixtures.

## P3 — Providers and distribution

- [ ] GitLab, Bitbucket, Azure DevOps PR URL adapters.
- [ ] Provider-specific fetch/head resolution; support fork PRs without assuming `origin`.
- [ ] Multi-repository settings UI, per-user installer, signed binaries.
- [ ] Diagnostics, opt-in performance traces, reproducible benchmark suite.
- [ ] Full Windows smoke test: installed userscript + browser + protocol handler + actual VS Code (separate slow CI/manual lane).

## CI resource usage

- GitHub Actions runs use concurrency grouping by workflow and branch, cancelling superseded runs.
- Markdown-only pushes and PR changes skip the expensive Windows test workflow.
- Prefer complete, meaningful code batches over many tiny `main` pushes.

## Test pyramid and time budgets (targets, not measured results)

| Suite | Scope | Run | Target |
| --- | --- | --- | --- |
| Unit | domain and use-case behavior | every change | < 10 s |
| Fast E2E | real local Git, snapshot restore, protocol, named pipe + fake editor | every PR | < 45 s |
| IPC integration | real named pipes and protocol launcher | every PR, Windows | < 10 s |
| Fault injection | interrupted snapshot/move/checkout | every PR after P1 | < 60 s |
| UI smoke | actual browser/tray/editor | nightly or release | < 3 min |
| Performance | large-file trees + optional ReFS | scheduled/manual | benchmark-driven |

On the 2026-10-21 Windows CI run after adding snapshot failure injection, 46 unit tests passed in about 1 s and 21 offline E2E passed in 46 s (test execution only, excluding build/restore). Time budgets remain goals, not hard guarantees. Fast E2E must not require network, GitHub tokens, VS Code, package install, or admin rights.

## Product metrics

- Time to Editor: click -> workspace opened.
- Time spent in fetch, checkout, artifact transfer and editor launch.
- Workspace hit/reuse rate; extra physical disk use.
- Data-loss incidents: **zero**.
- Crash recovery and failed-operation outcomes.
