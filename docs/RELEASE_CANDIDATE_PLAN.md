# Release Candidate Plan (RC1)

> Planning baseline: 2026-10-21. This is an implementation sequence, **not** a schedule for unattended/background execution. Every milestone must pass its exit gate before dependent, potentially destructive capabilities are enabled.

## Release objective and non-goals

**Product:** per-user Windows 11 .NET tray app + Tampermonkey userscript. Open a PR in the developer's editor with minimum **Time to Editor**, preserving user changes and reusing suitable Git worktrees and existing artifacts within a disk budget.

**Non-negotiable:** no user project builds, tests, `npm install/ci`, `dotnet restore`, CI execution, PR prediction or automatically generated changes in the application's runtime. Background activity may fetch **only explicitly configured** baseline refs (main/master/release branches) with bounded resource usage.

**Zero-data-loss principle:** do not recycle, clean, park or evict a workspace merely because an export exists. A full, tested restore and ownership transition is required. No implicit release when VS Code exits. Unknown, symlinked, junction, locked, modified-during-copy or otherwise unverified data causes a fail-closed decision.

## Milestones, dependency order and exit gates

| Phase | Effort range* | Deliverables | Mandatory gate |
| --- | --- | --- | --- |
| **M0 Foundation** | done | GitHub PR → URL protocol → user-scoped IPC → tray → allocated linked worktree → VS Code; offline Git E2E; trusted remote verification | CI green, no overwrite/reallocation of active worktrees |
| **M1 Snapshot and recovery hardening** | 1–3 eng-weeks | Versioned, verified snapshots; exact staged/unstaged/index/ignored/untracked/binary data; side-by-side restore; failpoints after all durable/FS side effects; interruption/low-disk/locked/reparse-point coverage; snapshot migration policy | Fault tests pass; source untouched on all failures; restored `git status` and content matches; unsupported cases explicitly rejected |
| **M2 Explicit release and park** | 2–4 eng-weeks | Durable session lifecycle with `Releasing`, `Warm`, `Parked`, `RecoveryRequired`; generation IDs and ownership leases; UI release confirmation; snapshots as recovery proof; atomic intent/verify/commit; parked session restore; crash and double-click recovery | No deletion while a session may still own paths; fault injection between **every** filesystem/Git/DB side effect; user changes survive release/park/restore |
| **M3 Warm-pool reuse** | 2–4 eng-weeks | Configured warm slots; cost-based candidate shortlist (file diff, lockfiles, artifact globs, measured transition cost); no PR forecasting; warm refresh for configured baseline refs only; safe Git-aware moves; reuse files without installing or building | Active sessions never touched; unsupported submodules/locked paths fail closed; measurable Time to Editor benefit vs fresh worktree on reference fixtures |
| **M4 Disk governance and artifacts** | 2–3 eng-weeks | Storage budgets, pinned slots, measured disk use, safe eviction of owned/verified disposable data; policy-driven preserve/reuse/copy/skip; safe hardlink prohibition for mutable files; optional ReFS block clone with fallback; user-initiated repair | Disk pressure cannot evict active, parked-unsafe or recovery-needed state; NTFS fallback; artifact rules tested with generated, ignored and unknown files |
| **M5 Provider adapters and usability** | 2–4 eng-weeks | GitHub, GitLab, Bitbucket and Azure DevOps adapters (including enterprise deployment origins, forks and PR refs where supported); secure repository matching; per-user settings/diagnostics; cancellation and progress; editor configuration | Host allowlists, permissions, URL parsing and trust tests for each supported provider; single-instance process smoke; basic accessibility/manual QA |
| **M6 RC hardening and packaging** | 2–4 eng-weeks | Per-user installer/uninstaller/upgrade, signed/reproducible release assets, semantic version/migrations, upgrade/rollback instructions; error telemetry **local and opt-in**; performance baseline, security review, support/runbook; docs | RC checklist below entirely green; no open critical/high data-integrity or privilege-boundary bugs |

*Engineering-week ranges are rough planning estimates for one experienced developer, not commitments; unknown monorepo behavior, provider specifics and filesystem edge cases may expand scope. Sequence and gates govern progression, not dates.

## Detailed execution backlog

### M1 — reliability (current phase)

- [x] Session ownership journal and interprocess serialization for PR acquisition.
- [x] Export snapshot with Git index, staged blobs, working files, ignored/untracked files, hashes and manifest.
- [x] Side-by-side restore into a new linked worktree without touching the active session.
- [x] Restore checks checksums and validates exact Git status; corruption/path traversal tests.
- [x] Injectable exception/fault checkpoints at snapshot export, publish and restore milestones; tests assert preserved source, retained partial data, retry and ownership.
- [x] Run real parent-initiated OS process kills at snapshot pre-publish/post-publish and restore worktree-created/index-applied boundaries; assert no original-data loss, persistent orphan state, and successful retry.
- [ ] Expand process-kill coverage to every remaining operation checkpoint and add low-space/concurrent-mutation tests before enabling cleanup.
- [x] Read-only inventory of pending exports, unjournalled/missing snapshots and unmanaged recovery worktrees, exposed in tray diagnostics.
- [ ] Add durable restore operation journal (in-progress / verified / orphaned); inventory alone does not permit automated cleanup.
- [x] Recheck status, index hash, file digests and enumerated paths immediately before snapshot publication; E2E verifies a last-moment working-file edit and concurrent Git index mutation both reject publication.
- [ ] Strengthen file identity, reparse-point race protection, and unsupported index extension/conflict handling before release or eviction.
- [ ] Prove low disk, ACL/read-only, anti-virus lock, long/unicode/case-collision path handling and cancellation.
- [ ] Migrate old manifest versions explicitly, never reinterpret unknown versions.
- [ ] Establish snapshot budgets/settings and retention policy; preserve originals until verified recovery.

### M2 — ownership and parking

- [ ] Model explicit allowed transitions and generation IDs (the pool never infers ownership from folder names).
- [ ] `ReleaseSession` use case: confirm → lock → inspect → snapshot and round-trip verify (where required) → journal intent → prepare disposition → verify → commit; otherwise `RecoveryRequired`.
- [ ] `ParkSession` use case: retain identity, staged/untracked/ignored, editor-associated context where safely possible; ensure enough disk space for snapshot.
- [ ] `RestoreParkedSession` with safe location allocation, exact content validation and explicit activation.
- [ ] Ensure moving worktrees updates Git metadata, with hard stop on unsupported submodules and Windows sharing violations.
- [ ] Reconcile all unfinished operations on restart; never auto-`git clean -fdx` or delete unknown paths.

### M3/M4 — reuse and disk

- [ ] Warm candidate estimator with cheap metadata first; bound expensive comparisons and learn from timing samples.
- [ ] Explicit artifact glob policies, with safe defaults for ignored files; `node_modules`, `bin/obj` remain **best-effort** reuse only.
- [ ] Optional ReFS clone or copy fallback; never hardlink mutable artifacts across sessions.
- [ ] Policy-scoped cleanup, eviction transaction journal and dry-run disk estimate; protected active/parked/recovery state.
- [ ] Configured baseline fetch with backoff, offline mode, no changed checked-out worktree and no arbitrary code execution.

### M5 — adapters and UX

- [ ] Provider-owned parsers and fetch-ref strategies; support HTTPS/SSH enterprise remotes via explicit per-host trust and registration.
- [ ] Browser Tampermonkey UI for supported PR pages and SPA navigation; reduce accidental permission scope.
- [ ] User-facing session list, status, progress, disk usage, pin/release/park/recover and diagnostics.
- [ ] Clear consent around external protocol invocations; no commands/paths embedded in untrusted browser input.

### M6 — release engineering

- [ ] .NET SDK, dependencies and Git minimum version pinned and reproducible.
- [ ] Signed per-user install, update and uninstall, URL protocol registration removal, SQLite schema upgrade/rollback.
- [ ] Windows Defender/AV smoke, NTFS/ReFS, multiple drives, locked files, Unicode, long paths, submodules, sparse checkout and Git LFS policies.
- [ ] Document safe recovery and support procedure before first RC.
- [ ] Launch performance/space benchmark against cold worktree on representative large repos; report medians and p95.
- [ ] Release notes, checksums and archived evidence from all RC gates.

## Test strategy (protect GitHub Actions capacity)

- Every PR: unit, architecture boundary checks, and **fast offline** Git/IPC/snapshot E2E; reuse shared local fixture setup where feasible. No browser, VS Code or network required.
- Every PR: targeted fault injection for transitions changed by that PR; keep timeout and fixture I/O budgets visible.
- Nightly or on demand (not every commit): matrix for NTFS/ReFS, long-path, simulated crashes, provider contract fixtures, large-file performance, install/uninstall.
- RC candidate: single full Windows end-to-end smoke including browser userscript + tray protocol + VS Code, clean per-user install/upgrade, and a 24h+ soak/restart test.
- GitHub Actions concurrency groups cancel superseded branch runs; docs-only changes skip Windows CI; aggregate related code edits before pushing.
- Record p50/p95 Time to Editor, snapshot/restore throughput, physical disk delta, branch switching errors and recovery events; avoid brittle single-sample thresholds.

## RC1 exit checklist — all must pass

- [ ] **Data safety:** zero known reproducible loss of tracked, staged, unstaged, untracked, ignored or binary user data; `RecoveryRequired` state never auto-cleaned.
- [ ] **Crash consistency:** kill at each documented state-changing step across checkout, snapshot, release, park, move, restore and eviction; recover or quarantine without data loss.
- [ ] **Ownership:** simultaneous clicks and two application processes cannot double-issue, overwrite or evict an active worktree.
- [ ] **Compatibility:** clean install and upgrade from previous preview; SQLite migrations and snapshot version handling verified; rollback/recovery documented.
- [ ] **Security:** no arbitrary code execution via protocol, Git hooks/filters or provider adapters; path traversal, reparse points and input length checked; least privileges; secrets never logged.
- [ ] **Functionality:** configured PR providers and editors demonstrated; release/park/unpark/pool eviction all covered by acceptance tests.
- [ ] **Speed and disk:** reference benchmarks document benefits and regressions, including cold-start fallback and a bounded disk budget. Optimize empirically; no unsupported universal speed claim.
- [ ] **User experience:** error, cancel, restore and disk-pressure actions clear and reachable from tray; no silent destructive action.
- [ ] **Release:** signed package, reproducible build, changelog, troubleshooting guide, upgrade/uninstall tests, test evidence, no open blocker/critical/high data-integrity issues.

**RC naming:** `v0.9.0-rc.1` only after all checklist items pass on the exact tagged commit. Fixes produce `rc.2`, etc.; stable `v1.0.0` is a separate decision after RC validation. Do not label an intermediate milestone an RC.

## Deferrals explicitly outside RC1

No predictive PR prewarming, no build/dependency install/test automation, no remote/cloud service, no CI orchestration, no exhaustive proof that reused build artifacts are compatible, and no generic Git engine rewrite. Optional ReFS acceleration may remain feature-gated with correct NTFS fallback if evidence is insufficient.
