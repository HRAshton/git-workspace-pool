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
- [x] Bound named-pipe message frames while reading, fail closed on oversized/unterminated input, and continue serving later requests. Offline IPC E2E covers both malformed cases.
- [x] Enforce a per-peer bounded read deadline so an idle local protocol sender cannot starve later launches; offline named-pipe E2E holds the malformed peer open and verifies the next valid request is received.
- [x] **Single-instance process E2E:** launch two isolated real tray processes; verify the OS mutex, user-scoped pipe forwarding and failure when the primary owns the mutex without a listener. No registry, settings or UI writes in the fixture.
- [x] Recover a pinned checkout after interruption; quarantine mismatching/unknown paths without cleanup.
- [x] Verify the configured GitHub clone's resolved `origin` (HTTPS or SSH) before allocating; reject lookalike hosts and mismatching repositories.
- [x] Reject effective Git URL rewrite and custom SSH/remote transport command settings before origin inspection and again before fetch. Do not inherit caller-provided Git SSH/askpass command overrides.
- [x] Refuse shell/path/argument-based credential helpers and nonempty core.askPass before origin lookup or fetch; accept bare named helpers. Drop inherited SSH_ASKPASS. Real offline Git E2E tests refusals and named helper compatibility. Trust of executable named helpers, config races, and other Git helper routes remain open.
- [ ] Extend remote identity validation to each newly supported Git provider.
- [x] Prevent concurrent or cross-process duplicate allocations via SQLite unique ownership and OS file locks.

**Gate:** tests cover create, reopen, unchanged local edits, reject unknown folders, invalid URLs, concurrent clicks, and interrupted Git operations. No network dependency in the fast lane.

## P1 — Session ownership and snapshots

- [x] Durable operation journal for PR acquisition: reservation, immutable commit pin, verification, activation, quarantine.
- [x] Extend the journal to record published snapshot receipts and audit events (SQLite schema v2).
- [x] Journal explicit release preparations/commits and parking/unparking moves with generation/ownership checks.
- [ ] Journal safe eviction only after an eviction policy and crash-consistent disposal workflow exist.
- [x] Minimal state machine for acquisition: preparing / active / recovery-required.
- [x] Add an explicit non-destructive suspended state; reopen resumes the exact owned worktree without deleting or moving files.
- [x] Test SQLite v3 → v4 schema upgrade preserving existing session, snapshot and restore foreign-key references.
- [x] Forbid automatic reactivation of quarantined `RecoveryRequired` sessions.
- [x] Add SQLite v6 protected slots and unique Held session/slot leases, backfilled transactionally for legacy sessions including parked owners.
- [x] Add generation-checked Ready-slot CAS reservation with concurrent SQLite tests; not yet called by the tray or allocator.
- [x] Add explicit Parked and Released states with safe generation-checked slot handoff, while retaining original parked worktrees.
- [ ] Introduce an automatic Warm lifecycle only after measured benefit and release/eviction safety gates.
- [x] Explicit parking/unparking through Git-aware worktree moves, preserving staged/ignored/untracked files and journal ownership; no release inferred from editor lifecycle.
- [x] Prevent nested and cross-column SQLite path ownership conflicts between active workspaces and reserved parked trees.
- [x] Detect Git submodule index entries before worktree relocation and refuse unsupported moves without changing session state.
- [x] Refuse untracked nested Git repositories before park/unpark intent and at the move boundary using a bounded non-following filesystem scan. Offline real-Git E2E proves separate repository files and leases are preserved; remaining relocation races still block automatic reuse.
- [x] Implement read-only, lock-scoped release eligibility assessment: require Parked state, Protected Held lease, exact Git registration and safe path ancestry; validate on real local Git.
- [x] Add bounded, read-only audit of protected/ready/reserved slot leases, owner identity and generation mismatches; expose conflicts through tray recovery diagnostics.
- [x] Journal version-7 release preparations under an OS lock and Git preflight; atomic slot/generation CAS, durable idempotent retry, and explicit cancellation. Block unparking while a preparation remains active.
- [x] Include all pending release preparations in read-only recovery diagnostics, with original owned paths and explicit cancellation instructions.
- [x] Complete the first explicit release handoff: SQLite v8 keeps Released PR snapshot/history and parked files while a locked, generation-CAS finalization atomically drops its original-path reservation, releases the Held lease and makes the original slot Ready. No automatic allocation or cross-session worktree reuse is enabled.
- [x] Implement explicit opt-in cross-PR Ready-slot allocation: SQLite atomically creates the new PR session and generation-specific Held lease; a verified checkout atomically activates the new owner. Real offline Git E2E demonstrates PR 18 in the former PR 17 slot while preserving PR 17's parked files and snapshots.
- [x] Reopen a PR placed in an explicitly released slot by resolving the persisted PR owner/path rather than inventing the conventional path; CI E2E exposed and drove this fix.
- [x] Add a locked, non-destructive restart recovery service for Reserved/Preparing slots after Git checkout but before activation: require persisted generation/owner, pinned HEAD, registration, safe ancestry and clean Git status; atomically activate on proof only. Real offline Git E2E covers clean, dirty and missing targets.
- [x] Reject ordinary PR reopen and legacy ActivateAsync for a Reserved slot: only a verified generation-CAS activation may transfer the owner from Preparing to Active. Fast E2E proves no files or leases are silently changed after interruption.
- [x] Inventory interrupted Reserved/Preparing allocations read-only in recovery diagnostics and warn in the tray at startup; show slot/generation/owner and require explicit recovery. No auto repair or deletion.
- [x] Expose a separately confirmed **Finish interrupted PR checkout (verified only)** tray action: select configured repository's exact Reserved generation and owner, pin selected lease/commit identity, lock and recheck, verify existing registered clean Git checkout, atomically activate and open editor. No file changes; reject stale, dirty, missing and unpinned targets. Real-Git E2E exercises successful recovery and refused selections.
- [x] Expose explicit parked-PR release, cancellation of pending release, and Ready-slot candidate selection in the Windows tray. Recheck trusted GitHub origin and slot generation before the fresh checkout; require separate user confirmations. Real-Git E2E covers selected-slot opening, retained parked data, refused PRs and stale selection.
- [x] Reopen an explicitly Released PR's retained parked worktree from the tray after another PR occupies its former slot: verify immutable parked path, Git registration and safe ancestry under the OS lock, allow user commits, and never move files or reacquire the original lease.
- [x] Route trusted browser/IPC PR-open requests for Released sessions through the same verified retained-worktree editor opening, not the normal allocator. Preserve cross-PR leases and fail closed on invalid path/registration; offline E2E covers PR A reopening after PR B owns A's old slot.
- [x] Add six real subprocess-kill handoff checkpoints across release preparation/commit and cross-PR reservation/HEAD pin/Git checkout/activation. Restore persisted state from a fresh process, prove original parked files and published snapshots survive, refuse incomplete checkouts and manually reconcile a proven clean checkout. The focused kill suite is a required PR CI step.
- [x] Add a manual synthetic npm reference benchmark that measures PR cold checkout, explicitly reused-slot checkout, copy time and bytes for 1,024 cached files. These are observed costs, **not** proof of improved Time to Editor or dependency-install acceleration.
- [x] Provide manual-only Windows workflow and PowerShell runner for repeated real Git/SQLite slot checkout and verified-copy samples, with per-trial JSON, nearest-rank p50/p95 and logical artifact bytes. Optionally compare offline warmed-cache npm ci (without scripts) with ordinary file copying on a trusted standalone npm project; do not treat raw copying as production verified copying or claim a speedup.
- [ ] Benchmark real npm dependency reinstallation vs compatible artifact copying, VS Code launch and browser-to-editor latency on Windows reference repos before making a speedup claim or enabling automatic recycling.
- [x] Implement explicit non-destructive snapshot export for staged/unstaged, untracked, ignored and binary files, with independent staged blobs and a Git index.
- [x] Restore a journalled snapshot into a separate Git worktree on the saved commit; verify index, working tree files and exact Git status.
- [x] Reject corrupted payloads and manifest path traversal before allocating a recovery worktree.
- [x] Enforce one 16 MiB manifest size limit before export publication, bounded restore deserialization and read-only recovery diagnostics. Fast offline Git E2E proves oversized manifests cannot publish or allocate recovery state.
- [x] Rehash the pending manifest, Git index, copied working files and staged payloads at the export publication boundary. Refuse modified evidence without publishing a receipt, changing source files or deleting pending evidence; offline Git E2E covers each payload kind. Filesystem replacement after final check remains an open race.
- [x] Discover interrupted exports, published-but-unjournalled snapshots, missing receipts and unmanaged recovery worktrees without deleting data; expose findings in tray diagnostics.
- [x] Persist restore intent before worktree allocation, verify after reconstruction, and distinguish interrupted/verified/missing recovery outcomes.
- [x] Report interrupted parking/unparking and missing parked worktrees in read-only diagnostics.
- [x] Reject snapshot/restore before allocation when the target disk fails a conservative free-space preflight.
- [x] Reject executable Git filter drivers before worktree checkout; disable Git LFS smudge/process as a safe identity fallback.
- [x] Restore snapshot directory attributes and timestamps after materializing child files.
- [x] Verify existing clean recovery files by SHA-256 using the same exclusive handle for comparison and overwrite; recheck clean Git status after baseline capture. Fast real-Git E2E protects competing editor changes between restored files.
- [x] Verify the recovery checkout Git index's clean SHA-256 on an exclusive handle before restoring the saved index; reject intervening `git add` changes without discarding staged user work. Real-Git E2E exercises the race after files are applied.
- [x] Reject unexpected filesystem occupants in isolated recovery worktrees even when Git status summarizes an already-ignored directory. Bound a non-following inventory before restoration and verify its permitted descendants before marking a restore Verified; offline real-Git E2E preserves foreign ignored files and the interrupted journal intent.
- [ ] Complete file identity/reparse-point races, deletion-path replacement, actual disk-full and safe restore-reconciliation tests before enabling automatic release or recycling.
- [ ] Configurable artifact retention and cleanup policies.
- [x] Tests for interrupted worktree creation (before journal commit) and cross-handler allocation.
- [x] Refuse snapshots of locked files without modifying the source or publishing a partial copy.
- [x] Simulate disk-full I/O failure *after* a working-file chunk is written during both export and side-by-side restore; E2E checks source preservation, pending/orphan evidence and safe fresh retries. Actual low-space/AV race coverage remains open.
- [x] Enforce the remaining snapshot byte budget while streaming staged Git blob content and working/index files, rather than after unbounded writes; kill the staged Git subprocess on any copy failure. Fast E2E covers oversized and exact-budget staged content.
- [x] Test cancellation after a real partial write on export and restore; retain pending/Preparing evidence and source bytes.
- [x] Read-only recovery inventory now checks saved Git HEAD, status, index hash and captured file hashes within a bounded scan; flags modified ignored binaries, Git index drift, missing evidence and changed HEAD without mutation. This is not an automatic repair or cleanup authorization.
- [x] Match saved staged Git blob hashes and manifest topology plus journalled byte/file counts during read-only inventory; corrupt stored stage payloads, duplicate aliases and manifest counter drift are marked unverifiable without touching original or recovered files.
- [x] Refuse an externally occupied restore target after Git checkout rather than overwriting a foreign file; use create-new for paths absent in the clean checkout. Read-only recovery checks enumerate captured ignored directory children to flag unexpected new ignored files.
- [x] Deterministic failpoints for snapshot export and side-by-side restore; confirm no source changes, orphan retention and successful retry.
- [x] Test parent-initiated process kills at all six snapshot/restore checkpoints in the manual slow lane; recover by retry without touching original user files.
- [x] Require an explicit supported snapshot manifest version on restore (missing, legacy and unknown future versions fail closed before checkout); migrations remain unimplemented.
- [x] Refuse snapshots with unmerged index stages, Git submodules or sparse index-directory entries before allocating snapshot storage.
- [x] Reject ambiguous Windows path aliases, reserved device names, malformed separators, reparse-point metadata and file/ancestor collisions before restore intent; detect dangling symlink ancestors using direct attribute queries.
- [x] Add real-process kill/fault coverage for release, Ready-slot handoff and park/unpark move boundaries.
- [ ] Add equivalent eviction crash/fault coverage if a safe eviction use case is introduced. See M1/M2 in the [RC plan](docs/RELEASE_CANDIDATE_PLAN.md).

**Gate:** every destructive transition can recover after simulated kill; unknown/user data is never silently removed.

**Slot ownership design gate:** [Exclusive slot ownership and safe release](docs/SLOT_OWNERSHIP.md). Only explicitly Released sessions relinquish their original slot reservation; ordinary Parked sessions retain exclusive ownership. An opt-in service can now allocate a Ready former slot to a second PR using a fresh checkout; automatic warm candidate selection and artifact transfer are not enabled.

## P2 — Reusable warm pool

- [x] Implement pure deterministic warm candidate ranking by exact HEAD, artifact invalidation and changed-path estimates; reject allocated, dirty, pinned, mismatched and recovery-required candidates.
- [ ] Collect verified on-disk candidates, measure transition cost, and connect the allocator only after exclusive slot release and ownership transfer are proven.
- [x] Add manual, copy-only `node_modules` reuse from a Released parked PR into a verified Active PR in the exact former slot. Require matching npm manifests, matching durable Git owner/source/HEAD, ignored/untracked paths, no links or destination collisions, and bounded per-file integrity checks. Expose explicit tray consent, bytes, file counts, and wall-clock transfer duration.
- [ ] Expand to configurable artifact classes (`bin`, `obj` etc.) only after a compatibility policy and safety tests; do not execute builds.
- [ ] Safe fallback for unsupported paths/filesystems.
- [ ] Optional ReFS block cloning where available.
- [ ] Disk budget and eviction based on size, usage, and recreation cost.
- [ ] Explicitly configured baseline branches (`main`, `master`, `release/*`) refreshed in the background, without checkout.

**Gate:** measured reduction in time-to-editor against cold checkout on representative large-file fixtures.

## P3 — Providers and distribution

- [ ] GitLab, Bitbucket, Azure DevOps PR URL adapters.
- [ ] Provider-specific fetch/head resolution; support fork PRs without assuming `origin`.
- [x] Add an unsigned, self-contained Windows x64 preview ZIP with versioned per-user installation, HKCU protocol registration, conservative unregistration, SHA-256 checksum, an included userscript, and a no-UI launch / idempotent-upgrade smoke in PR CI. Existing settings, snapshots, worktrees, and prior versions remain untouched.
- [ ] Provide signed MSI/MSIX or equivalent distribution, real Windows tray/browser/VS Code installed smoke, and supported upgrade/rollback from existing preview builds.
- [ ] Multi-repository settings UI and user-friendly setup beyond JSON configuration.
- [ ] Diagnostics, opt-in performance traces, reproducible benchmark suite.
- [ ] Full Windows smoke test: installed userscript + browser + protocol handler + actual VS Code (separate slow CI/manual lane). The first preview ZIP validates only self-contained process launch and upgrade-safe staging; it is not a GUI smoke.

## CI resource usage

- GitHub Actions runs use concurrency grouping by workflow and branch, cancelling superseded runs.
- Markdown-only pushes and PR changes skip the expensive Windows test workflow.
- Two-real-process Windows tray mutex/IPC forwarding and missing-listener E2E run in the FastE2E suite on each PR; no registration or UI is started.
- Focused six-boundary release/slot handoff real subprocess-kill E2E runs on each PR; the larger snapshot/parking crash suite remains manual under `workflow_dispatch` (and release validation). No duplicate CI is triggered on squash commits to `main`.
- The subprocess checkpoint signal reader retries Windows sharing violations, eliminating a race when the signal filename appears before its writer closes.
- PR CI also publishes a self-contained unsigned win-x64 preview ZIP, checks SHA-256 and performs isolated twice-installed startup smoke; release assets remain gated by CI and do not run on squash pushes to main.
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
