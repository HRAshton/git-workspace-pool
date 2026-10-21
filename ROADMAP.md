# Roadmap

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
- [ ] Worktree recovery on partial Git failures; no auto-clean of unknown paths.
- [ ] Validate registered Git remotes and reject mismatching/ambiguous repositories.
- [ ] Prevent concurrent or cross-process duplicate allocations.

**Gate:** tests cover create, reopen, unchanged local edits, reject unknown folders, invalid URLs, concurrent clicks, and interrupted Git operations. No network dependency in the fast lane.

## P1 — Session ownership and snapshots

- [ ] Durable operation journal: intent, validate, execute, verify, commit.
- [ ] Session state machine: preparing / active / released / warm / parked / recovery-required.
- [ ] Explicit release only. No release inferred from editor lifecycle.
- [ ] Preserve tracked/staged/untracked/binary/ignored files according to policy.
- [ ] Crash recovery and fault injection around each filesystem operation.

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

## Test pyramid and time budgets (targets, not measured results)

| Suite | Scope | Run | Target |
| --- | --- | --- | --- |
| Unit | domain and use-case behavior | every change | < 10 s |
| Fast E2E | real local Git, protocol, named pipe + fake editor | every PR | < 20 s |
| IPC integration | real named pipes and protocol launcher | every PR, Windows | < 10 s |
| Fault injection | interrupted snapshot/move/checkout | every PR after P1 | < 60 s |
| UI smoke | actual browser/tray/editor | nightly or release | < 3 min |
| Performance | large-file trees + optional ReFS | scheduled/manual | benchmark-driven |

On the 2026-10-21 Windows CI run, 23 unit tests passed in 60 ms and 9 offline E2E tests passed in 11 s (test execution only, excluding build/restore). Time budgets remain goals, not hard guarantees. Fast E2E must not require network, GitHub tokens, VS Code, package install, or admin rights.

## Product metrics

- Time to Editor: click -> workspace opened.
- Time spent in fetch, checkout, artifact transfer and editor launch.
- Workspace hit/reuse rate; extra physical disk use.
- Data-loss incidents: **zero**.
- Crash recovery and failed-operation outcomes.
