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
- [x] Cover all six implemented snapshot/restore process-kill checkpoints in manual slow E2E and exercise low-space/concurrent-mutation refusal in fast E2E. **This does not cover every filesystem side effect or enable cleanup.**
- [x] Read-only inventory of pending exports, unjournalled/missing snapshots and unmanaged recovery worktrees, exposed in tray diagnostics.
- [x] SQLite restore operation journal v3 commits intent before Git worktree allocation and verification after content/status checks; diagnostics distinguish interrupted, verified and missing restore destinations.
- [ ] Reconcile/repair orphan restore attempts safely; diagnostics never authorizes deletion or adoption. Bounded read-only checks now also validate saved staged-blob hashes, manifest topology, journalled file and byte counts, Git HEAD/status/index and captured working files; corrupt evidence is reported as unverifiable, never repaired or deleted.
- [x] Recheck status, index hash, file digests and enumerated paths immediately before snapshot publication; E2E verifies a last-moment working-file edit and concurrent Git index mutation both reject publication.
- [x] Restore recorded ignored directory creation times and attributes after descendant file writes; E2E covers ignored empty directories.
- [ ] Strengthen file identity, reparse-point race protection, and unsupported index extension/conflict handling before release or eviction. A post-checkout Git status guard now rejects unexpected occupants in a fresh recovery worktree and restores absent files with create-new semantics; remaining races/other index extensions remain unverified.
- [x] Add conservative free-disk preflight for snapshot and restore with injected low-space E2E; neither operation allocates a new directory when the probe reports insufficient storage.
- [ ] Prove actual mid-copy disk exhaustion, ACL/read-only, anti-virus lock, long/unicode/case-collision path handling and cancellation. Deterministic mid-copy IOException and cancellation tests verify preserved sources, interrupted records and retry. Manifest preflight rejects Windows path aliases/collisions; environmental races and cancellation at other boundaries remain outstanding.
- [x] Override Git hook path with a private empty directory and disable external FSMonitor for app-owned Git CLI calls; offline Git E2E covers malicious configured post-checkout hooks.
- [x] Refuse checkout if effective Git config contains any non-LFS executable filter driver (smudge/clean/process). Force LFS smudge/process to identity by command-line overrides and skip actual LFS downloads. Offline E2E covers PR creation and snapshot restoration refusal without touching existing user files.
- [x] Fail closed on effective URL rewrites, custom SSH commands, Git proxy commands and remote-origin transport/upload-pack overrides before resolved-origin inspection and fetch; sanitize inherited SSH/askpass command overrides. Real offline Git tests refuse these settings without checkout.
- [x] Reject unsafe Git credential.helper shell snippets, paths and arguments (including per-URL settings), plus nonempty core.askPass; allow bare named helpers and disable inherited SSH_ASKPASS. Validate effective configuration before origin resolution and fetch with real offline Git E2E.
- [ ] Complete Git authentication trust policy for executable named credential helpers, configuration mutation races and conditional include edge cases. Current preflight is not an atomic guarantee against concurrent config changes.
- [ ] Migrate old manifest versions explicitly, never reinterpret unknown versions. Current v2 restore now requires an explicit version and refuses missing/v1/future versions before Git or journal allocation; legacy migration is not yet supported.
- [ ] Establish configurable snapshot budgets/settings and retention policy; preserve originals until verified recovery. Current fixed conservative 2 GiB snapshot cap now applies during working/index and staged-blob stream writes; staged exporters terminate Git subprocesses on failure.

### M2 — ownership and parking

- [x] Add a strictly non-destructive `Suspended` session state: explicit tray command keeps the same owned worktree and resumes on opening its PR; SQLite migration v4 and E2E verify no file changes.
- [x] Prevent automatic reactivation of `RecoveryRequired` sessions; only an explicitly audited recovery may clear quarantine.
- [x] SQLite v6 protected slot registry and Held generation-aware leases: transactional backfill of existing sessions, atomic new session registration, unique live leases and CAS reservation with stale-generation rejection.
- [x] Add durable, idempotent SQLite v7 release preparations after locked Git/slot preflight; pending operations block unparking, and cancellation restores the ability to unpark without changing files.
- [x] Implement the explicit, non-destructive Ready-state handoff for an already verified Parked session: schema v8 preserves immutable original paths and snapshots, excludes only Released rows from the old-slot reservation index, and atomically commits the prepared generation/lease handoff after locked Git preflight.
- [x] Implement a narrowly scoped, opt-in Ready-slot acquisition and cross-owner Git checkout: an atomic CAS creates a new Preparing PR session with Held lease, verifies a clean target and Git commit, then atomically marks Active/Protected. Never silently delete or recycle earlier PR data.
- [x] Add explicit crash-restart reconciliation of an already checked-out Reserved/Preparing slot: under the original OS path lock, require matching SQLite generation/lease/session, trusted root and source, pinned commit, live Git registration and a completely clean checkout before promoting to Protected/Active. Missing and dirty targets are retained unchanged; the manual recovery path never runs checkout or cleanup.
- [x] Resolve PR reopening against existing immutable session ownership rather than a computed default path; the explicit cross-PR slot owner remains authoritative.
- [x] Add startup warnings and a bounded read-only recovery inventory for Reserved/Preparing generation/lease owners, surfaced through the tray diagnostics. Refuse ordinary PR reopening and legacy activation for these reservations so only explicit verified CAS reconciliation may activate the slot.
- [x] Add an opt-in **Finish interrupted PR checkout (verified only)** tray menu: display configured pending PR/slot/generation/pinned commit, require confirmation, lock and verify the exact selected lease/commit and registered clean Git checkout, then CAS-promote and launch editor. Missing, dirty, stale or unpinned reservations remain untouched; no Git checkout, reset or deletion.
- [x] Provide the first end-user opt-in slot workflow in the tray: park a PR, explicitly release its original slot with durable prepare/commit, select a Ready slot and enter a trusted GitHub PR URL. Cancel uncommitted release intentions from the tray. Reuse only a safe, empty slot path through a new checkout; previous parked user data remains immutable.
- [x] Exercise actual subprocess kills after durable ReleasePrepared, ReleaseCommitted, SlotReserved, CommitPinned, WorktreeCreated and SlotActivated checkpoints. Verify the earlier owner's parked files and snapshots, pending exclusive lease on interrupted acquisition, clean-checkout-only recovery, committed-operation retry and completed ownership; run the focused kill suite on PR CI.
- [x] Add explicit, opt-in `node_modules` reuse from a proven Released donor to a proven Active owner of the exact old slot, requiring equal package and lock files, Git ignore/untracked checks, no symlinks or collisions, bounded integrity-verified CreateNew copies, and a synthetic timing fixture. The parked original is never moved or deleted.
- [x] Add an opt-in Windows runner for repeated real-Git/SQLite checkout and production verified artifact-copy measurements (raw samples, p50/p95, bytes), plus an optional local warmed-offline npm ci versus raw file-copy baseline. No automated PR benchmarks, no data deletion and no unmeasured speed claims.
- [ ] Run installed Windows browser/tray/editor smoke and compare real npm install, file-copy, and VS Code Time to Editor before enabling automatic reuse or wider artifact policies.
- [ ] `ReleaseSession` use case: confirm → lock → inspect → snapshot and round-trip verify (where required) → journal intent → prepare disposition → verify → commit; otherwise `RecoveryRequired`.
- [x] Add explicit whole-worktree parking using `git worktree move`, a private destination on the same root, durable SQLite intent before move, and no file deletion.
- [x] Add explicit unparking, including recorded return intent, occupied-destination refusal, and recovery after interrupted moves.
- [x] Add real OS subprocess-kill tests for all four move boundaries, checking durable intent, unchanged user data, and safe retry under new lock ownership.
- [x] Read-only diagnostics now identify interrupted parking/unparking and missing or unverifiable parked trees without changing the working files.
- [x] Guard reservation and parking transactions against nested and cross-column path overlaps; test that other PR sessions cannot claim existing parked directories.
- [x] Fail closed on staged/committed Git submodule index entries before recording a parking intent; offline Git E2E verifies no path or journal mutation.
- [x] Refuse nested Git repositories/gitfiles in untracked or ignored paths before park/unpark intent and Git move, with a bounded non-following scan and real offline Git E2E.
- [ ] Harden locked paths, external editor races, cross-process moves and filesystem replacement during relocation before automatic slot reuse.
- [x] Offer a verified editor reopen of a Released session's immutable retained parking path, without moving any worktree or reclaiming its released slot. An edited/committed retained Git worktree may be reopened; an unregistered/missing tree fails closed.
- [ ] `RestoreParkedSession` with safe location allocation, exact content validation and explicit activation.
- [ ] Ensure moving worktrees updates Git metadata, with hard stop on unsupported submodules and Windows sharing violations.
- [ ] Reconcile all unfinished operations on restart; never auto-`git clean -fdx` or delete unknown paths.

### M3/M4 — reuse and disk

- [x] Implement a bounded and deterministic, side-effect-free candidate-ranking policy that never mutates worktrees or decides ownership; prefer exact commit, then estimated artifact invalidation, tracked paths and optional observed duration.
- [ ] Collect trusted Git/disk candidate observations and connect the policy to a transactional allocator only after M2 slot-release gates are complete. Benchmark transition time and re-rank using measured costs.
- [ ] Explicit artifact glob policies, with safe defaults for ignored files; `node_modules`, `bin/obj` remain **best-effort** reuse only.
- [ ] Optional ReFS clone or copy fallback; never hardlink mutable artifacts across sessions.
- [ ] Policy-scoped cleanup, eviction transaction journal and dry-run disk estimate; protected active/parked/recovery state.
- [ ] Configured baseline fetch with backoff, offline mode, no changed checked-out worktree and no arbitrary code execution.

### M5 — adapters and UX

- [x] Add a real two-process Windows tray mutex/pipe forwarding E2E and bounded missing-listener failure check in PR CI, without user settings, protocol registration or GUI startup. Installed UI/browser/editor smoke remains open.
- [ ] Provider-owned parsers and fetch-ref strategies; support HTTPS/SSH enterprise remotes via explicit per-host trust and registration.
- [ ] Browser Tampermonkey UI for supported PR pages and SPA navigation; reduce accidental permission scope.
- [ ] User-facing session list, status, progress, disk usage, pin/release/park/recover and diagnostics.
- [ ] Clear consent around external protocol invocations; no commands/paths embedded in untrusted browser input.

### M6 — release engineering

- [ ] .NET SDK, dependencies and Git minimum version pinned and reproducible.
- [x] Build an unsigned self-contained preview ZIP in PR CI with bundled userscript, SHA-256 checksum, per-user versioned install, explicit HKCU protocol registration/unregistration, and isolated no-UI smoke including a second idempotent install that preserves unknown existing files. Existing preview binaries and all SQLite/settings/worktrees are left alone.
- [ ] Signed per-user installer/update/uninstall, trusted release publishing, real tray/browser/editor installed smoke and safe downgrade/SQLite rollback proof.
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
- [ ] **Compatibility:** packaged preview clean install and same-package reinstall are smoke-tested; migration of an actual older user database, moving between signed releases, and rollback/recovery still need a manual Windows acceptance test.
- [ ] **Security:** no arbitrary code execution via protocol, Git hooks/filters or provider adapters; path traversal, reparse points and input length checked; least privileges; secrets never logged.
- [ ] **Functionality:** configured PR providers and editors demonstrated; release/park/unpark/pool eviction all covered by acceptance tests.
- [ ] **Speed and disk:** reference benchmarks document benefits and regressions, including cold-start fallback and a bounded disk budget. Optimize empirically; no unsupported universal speed claim.
- [ ] **User experience:** error, cancel, restore and disk-pressure actions clear and reachable from tray; no silent destructive action.
- [ ] **Release:** an unsigned PR preview ZIP and conservative installer exist, but signed release artifacts, reproducible-build attestations, manual tray/browser/VS Code smoke, upgrade from real user data, and open high-risk safety gates remain unfinished.

**RC naming:** `v0.9.0-rc.1` only after all checklist items pass on the exact tagged commit. Fixes produce `rc.2`, etc.; stable `v1.0.0` is a separate decision after RC validation. Do not label an intermediate milestone an RC.

## Deferrals explicitly outside RC1

No predictive PR prewarming, no build/dependency install/test automation, no remote/cloud service, no CI orchestration, no exhaustive proof that reused build artifacts are compatible, and no generic Git engine rewrite. Optional ReFS acceleration may remain feature-gated with correct NTFS fallback if evidence is insufficient.
