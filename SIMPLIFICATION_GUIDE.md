# Simplification Rewrite Guide: In-Place Warm Worktree Reuse

> Status: implementation specification for follow-up coding agents. Documentation only; no code changes are authorized by this file.
>
> Repository: `HRAshton/git-workspace-pool`. Update the **existing** application and solution, not a new repository or product. The application has not been released, so new runtime state need not read old SQLite schemas. **Never assume that unreleased means local development data can be discarded.**
>
> Read this guide before changing implementation. Treat its invariants and acceptance tests as the target contract. Earlier roadmap and architecture documents describe the old implementation, not required behavior for this rewrite.

## 0. One-sentence objective

**From a browser click, open the requested Git repository + feature branch in VS Code as quickly as practicable by reusing a *real, already populated worktree* and changing the fewest necessary Git files, while retaining reusable pre-restored packages and build artifacts.**

This is **not** a general-purpose snapshot manager, cache-copying service, or "new worktree launcher."

## 1. User requirements and non-negotiable success criteria

1. **Browser-first.** A versioned `prworkspace://` link carrying a trusted repository URL and short branch name starts the workflow without a provider REST API.
2. **Minimal actual filesystem transition.** A repository can contain thousands of commits and files. Choose the closest safe *physical* warm worktree, then switch **in that same directory** rather than allocating an empty path and checking out everything anew. Commit count/graph distance alone is not a file-change or latency metric.
3. **Keep existing artifacts.** Warm directories may already contain dependencies, restored packages, generated files, and incremental build outputs. Preserve non-conflicting ignored artifacts in place. Never silently delete, replace, or copy them as a normal transition.
4. **Both donor types matter.** A warm worktree may come from this feature branch's previous work or from a nearby major/baseline branch. A single selection/transition algorithm handles both; do not build parallel "feature donor" and "major donor" engines.
5. **Do no harm.** An owned workspace with edits, staged content, untracked content, local commits, uncertain Git registration, or an interrupted transition must not be silently reassigned.
6. **Prove performance.** Measure actual checkout and browser-to-editor latency on representative large repositories. A plausible diff-based ranking is only a heuristic until measured.

### Explicit non-goals (v1)

Snapshots, exact index recreation, parked worktree moves, released *empty* slots, reusable-slot leases and generation CAS, auto-eviction, automatic deletion, artifact copying across worktrees, custom npm/NuGet restore, build/test execution, predictive warming, provider PR APIs, branch regex discovery, background network polling, full rebuild orchestration, and cross-version SQLite migrations.

A **manual/preconfigured "prepare warm worktree"** operation is in scope. Aggressive unattended background automation is not.

## 2. The most important correction to the current code

The present `VerifiedReuseCandidateCollector` chooses a `Ready` **empty slot** linked to a separate parked donor. `AcquireExplicitReadySlotService` then creates a **new checkout** at the empty slot. `VerifiedNpmArtifactCopier` can optionally copy `node_modules` afterward.

That is *not* the desired warm transition. Even an exact-head donor is not checked out in place, and copying only npm artifacts does not preserve arbitrary cached build outputs.

**Replace this design** with: the candidate **is the existing registered worktree directory itself**; the workspace gets reassigned **in place** after explicit availability and safety checks.

Do not retain the old slot/release/park machinery merely to reuse its data models.

## 3. Target behavior and ownership rules

### Browser open: ordered decisions

1. Strictly parse `prworkspace://open/?v=2&repo=...&branch=...`.
2. Resolve only an explicitly registered/trusted repo, source clone, workspace root, and editor.
3. Acquire an interprocess **repository-level coordination lock**; prohibit two application processes from selecting the same available donor concurrently. The lock must cover selection, transition, and publication. Other app entry points must use it too.
4. If this repo/branch already has an **Assigned** worktree:
   - Verify identity, path, Git registry, and basic ownership consistency.
   - **Open it unchanged**; no checkout, reset, clean, refetch-for-update, or artifact invalidation. Preserving ongoing edits and local commits takes priority over a newer remote head.
   - If missing/ambiguous, fail safely and flag manual recovery.
5. Otherwise, fetch the requested remote branch to a **dedicated app ref**, resolve and pin a full target commit ID. Use the existing trustworthy noninteractive Git transport rules.
6. Find available, already-populated worktrees from the same trusted local source clone/repository.
7. Compute a cheap ranking, then validate a small shortlist. Prefer the safest/lowest estimated transition cost; do not examine or hash every cached file on every browser click.
8. If a suitable candidate is found, persist a small durable `Transitioning` intent **before** mutating its Git state; verify clean tracked state, non-conflicting untracked/ignored entries, expected old HEAD and registration; switch to the pinned target in **that same directory**.
9. Verify target HEAD, Git registration, correct owner/path, and Git status; publish `Assigned` only after success.
10. Launch the configured VS Code executable. Failure to launch the editor does not roll back the valid workspace.
11. If no safe donor is available, create a new worktree at an *unoccupied managed location*, then assign it. **Never** recycle an occupied unknown directory or silently steal an assigned one.

### Three stable states (plus one transient state)

| State | Contract |
| --- | --- |
| `Available` | App-managed, registered, clean tracked checkout at known HEAD; ignored compatible artifacts may exist. May be selected for in-place transition. |
| `Assigned` | Owned by one feature request, protected against reassignment; reopen unchanged even if dirty or locally advanced. |
| `Quarantined` | Missing/inconsistent/uncertain worktree or interrupted operation. No automatic checkout, cleanup or reuse. |
| `Transitioning` | Durable intermediate intent while taking a worktree from Available to Assigned. Leftover Transitioning rows become Quarantined on startup or require **read-only** verification before any explicit manual recovery. Never infer success from path existence alone. |

No automatic "detect idle VS Code and release" logic. An assigned workspace can become Available only by an **explicit release** action whose checks prove it is safe. In v1 this can be a CLI/tray action, not a parking operation.

### Release contract

Before releasing, verify: source clone and registration still match; no staged/unstaged/conflicted/untracked user data (except declared ignored artifacts); no ongoing operation; and no unprotected local commits. **Prefer rejecting release if HEAD differs from the commit recorded at assignment**, even when `git status` is clean. Do not silently orphan detached-HEAD commits. If a user creates or changes a local branch, keep the workspace Assigned until an explicit safe procedure can prove all changes/commits are retained elsewhere.

Never perform `git reset --hard` or `git clean` as part of release. If eligibility fails, the workspace stays Assigned and the UI explains why.

## 4. Filesystem/Git transition safety

### In-place transition

- Work inside the chosen **existing worktree path**, not a parked clone or a freshly allocated empty slot.
- Use a pinned immutable target commit, ideally a detached checkout for app-managed pool transitions to avoid a Git branch being simultaneously checked out in multiple worktrees. The app must not assume detached HEAD means there are no user commits.
- Git can usually retain unchanged tracked files when switching commits; actual writes depend on checkout mechanics, index attributes, file modes and filters. **Measure rather than promise exact zero rewrites.**
- Run Git through `ProcessStartInfo.ArgumentList` (no shell-string composition).
- Candidate command shape for **disposable fixture tests only**, after prechecks and transport restrictions: `git -C <worktree> checkout --detach --no-overwrite-ignore <full-target-sha>`. Validate actual supported Git syntax in the pinned test environment. A higher-level Git switch command is also fine if it demonstrably preserves the same safeguards.
- `--no-overwrite-ignore` is important because checkout otherwise may overwrite ignored files that conflict with incoming tracked files. A candidate with such conflicts should be rejected rather than cleaned.
- Inspect meaningful conflicts **before** mutation; Git's refusal is an additional barrier, not the whole safety design.
- Successful checkout is not enough: verify expected HEAD, registered path and clean tracked state before marking Assigned.
- If checkout fails or process dies partway through: **Quarantined**, preserve the entire directory, no hidden retry, reset, checkout-back, deletion, or automatic reuse.
- Avoid worktree checkout in repos with unsupported sparse checkout, submodules, custom executable smudge/clean filters, or other semantics not yet covered by integration tests. A clear "unsupported for warm transition; fresh or manual" response is better than a silent destructive fallback.

### Forbidden operations on existing user directories

Never run `git clean -fdx` / `git clean -ffdx`, `git reset --hard`, `git worktree remove --force`, unconditional `Directory.Delete`, unconditional `Move`/copy, or rewrite ownership metadata merely to make a candidate eligible. Tests may use disposable fixtures, but production must not silently discard data.

### Trust boundary

Keep current defenses in `GitBranchRequest` / `GitBranchProtocol`, `GitProcessSafety`, `BoundedGitProcess`, source-clone/origin verification, path containment and Windows junction/reparse-point checks. Browser input must never specify arbitrary local filesystem paths, shell commands, refspec destinations, trusted repositories, or executable paths. Keep credential helpers/SSH agent and strict host verification where supported; never weaken TLS or host-key checking to make "fast" work.

Network fetch must not update the source checkout or other user's refs/worktrees. Pin requested commits using dedicated application refs, not moving `FETCH_HEAD`. Disable untrusted checkout hooks/filters and implicit execution as currently done. Timeouts, bounded output, cancellation and noninteractive authentication are useful safeguards, not features to delete.

## 5. Ranking: optimize the real cost, not ancestry distance

A worktree is a feasible candidate only if it is Available, shares the trusted source/object store, is currently clean by policy, is registered at its recorded path/HEAD, and is not blocked by tracked/untracked/ignored collisions.

**Stage A: cheap estimate** (cached observation, few Git calls)

- Exact target HEAD first when eligible.
- Estimate changed tracked paths using a bounded tree comparison of **donor HEAD vs pinned target HEAD**; e.g. `git diff --no-ext-diff --no-textconv --no-renames --name-only -z <donor> <target> --`.
- Optionally estimate changed blob bytes / paths with large tracked files if cheaply available. Compare *tree differences*, not commit count or common-ancestor distance.
- Consider cached artifact-input compatibility and recent **measured** transition durations, but do not invent arbitrary weights that defeat real test evidence.
- Exclude uncertain candidates; do not treat missing inspection results as zero cost.

**Stage B: shortlisted final check**

- Revalidate donor under the repository lock, not based solely on stale preview UI.
- Inspect tracked, staged, untracked and ignored collisions; verify worktree state and HEAD, source identity, reparse/symlink safety, and artifact policy.
- Compare expected candidate/target fingerprints; reject unsupported transitions before Git changes anything.
- If best candidate fails, re-evaluate another safe candidate or use fresh fallback; never auto-repair or clean it.

**Cost model (conceptual only):**

~~~text
estimatedCost =
    checkoutTrackedWriteCost
  + expectedArtifactInvalidationCost
  + requiredPreparationCost
  + observedFixedOverhead
~~~

Use lexicographic deterministic ranking initially: eligibility → exact HEAD → compatible artifacts → fewer changed tracked paths → observed latency → stable tie-break. Iterate toward a weighted predictor **only after real benchmarking**.

A donor from a major baseline is simply another Available worktree. A formerly assigned feature worktree may also become Available, but only through explicit safe release. No separate baseline state machine is required.

## 6. Artifacts: preserve bytes, but distinguish presence from correctness

- Ignore/package/build artifacts remain **physically in the selected worktree** when no path collision occurs. The v1 transition does not copy `node_modules`, `bin`, `obj`, restore folders, etc. from a different donor.
- Do not assert that arbitrary build products are valid just because Git preserves the files. A branch change can invalidate cached dependencies/builds even if the cache directory still exists.
- Support a small **optional per-repo artifact input policy** (prefer documented paths/globs, not a plugin system). Examples: dependency lockfiles, package/toolchain descriptors, project or solution metadata, build-config files. Compare their Git tree blob IDs / a deterministic digest from donor vs target. Missing/config-changed inputs ⇒ "unknown/incompatible", not a claim of validity.
- Avoid full cache-folder hashing or recursive size scans on the click path. Record a lightweight input fingerprint when preparing/warming a worktree; verify against pinned commit metadata. Revalidate assumptions as necessary at transition.
- If policy declares some artifacts unsafe to keep/reuse for a target, **rank the candidate lower or reject it**. Do not silently delete them. For *unknown* arbitrary build outputs, document that the build tool remains responsible for staleness detection.
- An ignored path in the donor that becomes tracked in the target is **not** safely reusable: reject if it would be overwritten. Cached artifacts may include junctions/symlinks (e.g. some package managers); do not follow those during inspection. Define an explicit supported policy instead of assuming all cache trees are ordinary files.
- Optional future feature: user-initiated cache cleanup/rebuild in a **disposable** or explicitly confirmed workspace, separate from browser open. Out of scope for initial rewrite.

Artifact savings should be measured by *files/bytes retained and restore/build work avoided*, not simply how many cached bytes exist.

## 7. State, configuration and locking

**Reuse existing project structure**, but replace old domain/application code as needed. Keep a small ownership registry in SQLite **from a fresh schema** rather than extending v1–v9 session/slot tables.

Suggested v1 schema (adapt names if clearer; avoid over-modeling):

~~~text
workspaces(
    workspace_id,
    repository_key,
    source_clone_path,
    absolute_worktree_path UNIQUE,
    state,                        -- Available/Assigned/Transitioning/Quarantined
    assigned_branch NULL,
    recorded_head,
    transition_target NULL,
    artifact_input_fingerprint NULL,
    updated_utc
)
~~~

Register repos, local source clone/workspace root and editor in existing JSON settings; avoid duplicating that configuration in SQLite. Persist only minimal ownership/transition evidence. Schema constraints must ensure no two Assigned rows own the same repo+branch, no duplicate path ownership, and no cross-repository reuse. Enforce path containment and disjointness independent of DB strings.

Use the existing `FileWorkspaceOperationLock` concept, ideally one **repository-wide** lock for selection/transition/assignment to avoid two browsers claiming a donor in parallel. Crash releases OS lock, but retained Transitioning evidence must **not** be automatically converted to Available. Keep transactions small; never pretend a SQLite transaction makes Git or filesystem mutation atomic.

Old development databases are not migrated. **Do not overwrite existing state on startup.** Pick a distinct v1 state path/name or require documented, manual clean-profile setup after a backup and inventory. If old app-owned paths/worktrees exist, refuse implicit adoption. Provide a read-only legacy inspection/export path if needed, not a hidden destructive migration.

## 8. Source-level retention/removal plan

### Retain and adapt (inspect before modifying)

- `src/GitWorkspacePool.Core/Domain/GitBranchRequest.cs` and `Core/Protocol/GitBranchProtocol.cs`: canonical request semantics and strict v2 protocol.
- `src/GitWorkspacePool.Infrastructure/GitCliWorkspaceGit.cs`: source clone validation, noninteractive fetch, worktree registration and pinned refs; **add** verified in-place transition operations.
- `src/GitWorkspacePool.Infrastructure/GitProcessSafety.cs` / `BoundedGitProcess.cs`: security, process limits.
- `src/GitWorkspacePool.Infrastructure/FileWorkspaceOperationLock.cs`: serialize operations, extend to repo scope as appropriate.
- `src/GitWorkspacePool.Tray/Program.cs`, protocol registration, pipe/single-instance handling **if still justified**, `VsCodeLauncher.cs`, JSON settings, registration forms and installer basics.
- Existing Windows Git E2E fixtures and process-crash tests: salvage relevant assertions, refactor rather than retaining old slot-specific test requirements.

### Rewrite

- `Core/Application/OpenGitBranchHandler.cs` → small coordinator: reopen existing assignment; otherwise pin target, select available worktree, transition or fresh fallback, open editor.
- `Core/Application/WarmPool/*` → **small** selection/validation and ownership service, not an allocator with generations and two-phase released-slot handoffs.
- `Infrastructure/SqliteWorkspaceSessionJournal.cs`, `SqliteWorkspaceSlotRegistry.cs`, `SqliteGitBranchSessionStore.cs` → replace old journal complexity with **one small new schema/adapter**. Remove obsolete interfaces instead of emulating old APIs.
- `Tray/TrayContext.cs` → slim menu with Open branch, Prepare warm workspace, List workspaces, Release eligible workspace, Diagnostics, Settings, Exit. Preserve browser open UX.

### Remove once replacement tests pass

- `Infrastructure/Snapshots/*`, capture/restore services, snapshot/restore receipts/journals, scanner-heavy recovery UI.
- `ParkWorkspaceSessionService.cs`, suspend, park/unpark, explicit released-slot allocation/reconciliation, lease generation/CAS, release preparation journal and handoff checkpoints.
- `VerifiedNpmArtifactCopier.cs`, `VerifiedReuseCandidateCollector.cs` (empty-slot donor model), obsolete copying/ranking/diagnostics.
- `RefreshConfiguredBaselinesService.cs`, regex baseline discovery and timer/backoff/fairness scheduler. Support a small explicit list of major branch names as **manual warm-up sources**, without automatic scans.
- Old PR provider test-support adapters, compatibility-only handlers, obsolete settings and code, migration code, storage warning dashboards, opt-in detailed timing UI.
- Superseded RC/historical feature documentation can move under an archive heading/folder. Do not delete documentation needed to recover old local worktrees.

Avoid deleting safety helpers used by the new path simply because they currently sit under `Snapshots/`. Extract only what is still needed (e.g. safe path checks) into a small general-purpose module.

## 9. Minimal interface sketch

Interfaces below describe responsibilities, **not** a requirement for one interface per class or a framework:

~~~csharp
record OpenRequest(string CloneUrl, string Branch);
record Candidate(string WorkspaceId, string Path, string Head, string? ArtifactFingerprint);
record TransitionEstimate(int ChangedTrackedPaths, bool ArtifactsCompatible);

interface IWorkspaceRegistry
{
    // Find assigned worktree, enumerate available, record transition intent,
    // commit assignment, quarantine, and explicitly release.
}

interface IWorktreeGit
{
    // Fetch pinned target ref, inspect registered HEAD/status,
    // diff donor and target trees, transition an existing clean checkout,
    // create a fresh checkout when no donor is eligible.
}
~~~

Prefer a small, directly testable service with a short happy path. Do not invent another generic workflow engine, command bus, notification framework or broad dependency injection hierarchy.

## 10. Implementation plan for coding agents

**Each phase should be an independently reviewable PR/commit with passing tests.** The source paths above reflect the repository at the time this guide was written; adapt to new edits rather than assuming line numbers.

### Phase 0: preserve evidence, establish measurements

- [ ] Create an archival Git tag/branch for current working code, not an archive of user runtime data.
- [ ] Document current installed-data locations and a **read-only** backup/inventory procedure for source clones, worktrees, SQLite, snapshots and app-owned Git refs. Never test on an unbacked-up real user worktree.
- [ ] Build/test the current tree on pinned Windows .NET SDK; record current integration baseline and failures.
- [ ] Record cold vs existing-feature reopen latency on disposable **representative large repositories**, including file count, Git tree delta and cache footprint. No invented performance claims.

### Phase 1: introduce the new minimal ownership engine

- [ ] Implement v1 workspace schema with a **new data path** and constraints.
- [ ] Build repository lock, safe worktree inventory and read-only diagnosis for inconsistent or unknown paths.
- [ ] Build explicit Available/Assigned/Transitioning/Quarantined transitions with persisted intent and no automatic cleanup.
- [ ] Verify old runtime data cannot be silently imported, overwritten, deleted or claimed.
- [ ] Unit/integration tests for concurrency, schema constraints, stale path/HEAD, and interruptions.

### Phase 2: prove real in-place checkout

- [ ] Add one explicit operation `transition(availableWorktree, pinnedTarget)` on a disposable existing worktree containing ignored dependency/build artifact sentinels.
- [ ] Check the complete safety contract; reject dirty/conflicting/unsupported candidate **before** mutation.
- [ ] Switch in place, preserve safe ignored sentinels, verify HEAD/registration/tracked cleanliness, publish ownership.
- [ ] On failure, quarantine and preserve bytes. Test killed process after durable intent and during Git checkout.
- [ ] Demonstrate **same physical path/inode identity where applicable** and no full fresh `git worktree add`/copy for selected donors.

### Phase 3: ranking and baseline warm-up

- [ ] Add cheap donor-to-target tree diff ranking and bounded shortlist checks.
- [ ] Add optional lightweight artifact-input fingerprinting; do not claim arbitrary build correctness.
- [ ] Add explicit "prepare worktree for branch X" command/UI for selected major branches.
- [ ] Test feature-donor vs closest-major-donor, exact-head reuse, compatibility mismatch, unknown candidate and fresh fallback.
- [ ] Collect timings and filesystem change counters; tune only from observed data.

### Phase 4: browser/tray integration

- [ ] Keep v2 protocol contract and registration/origin trust checks.
- [ ] Wire browser click to existing Assigned reopen or safe in-place transition.
- [ ] Simplify menus/forms. Offer clear errors for Quarantined, dirty release, missing source, authentication, unsupported cache contents.
- [ ] Prevent reentrant clicks from allocating the same candidate; ensure single-instance and repo lock agree.
- [ ] Test browser→tray→Git→VS Code on the target Windows environment.

### Phase 5: delete obsolete code and align docs

- [ ] Remove old snapshots/parking/released-slots/leases/npm-copy/baseline scheduler implementations and tests once their replacements work.
- [ ] Remove stale dependency-injection registrations, settings fields, package/scripts/CI assumptions and menus.
- [ ] Replace `README.md`/`ROADMAP.md`/`docs/ARCHITECTURE.md` with the **actual new behavior**; clearly distinguish implemented from planned.
- [ ] Keep Windows build + useful Git integration/crash/package tests. Do not carry every old release attestation/documentation stage unless required for real distribution.
- [ ] Search for dead references to old states and ensure no implicit user-data deletion in installation/update/uninstall scripts.

## 11. Mandatory acceptance test matrix

Use disposable Git repositories and isolated temporary profile paths. At least:

| Scenario | Required result |
| --- | --- |
| Browser opens assigned feature twice | Same directory; local commits, staged/unstaged/untracked/ignored bytes preserved; no checkout on reopen |
| Exact-head available donor | Assign physically existing directory; no new worktree; artifacts survive |
| Nearby major-branch donor | Only target-tracked differences applied; compatible ignored artifacts survive |
| Many-commit history, small tree diff | Rank by actual tree delta, not raw commit count |
| Two donors: small Git diff but incompatible dependencies vs larger diff with compatible caches | Compatibility affects selection; never misreport incompatible cache as valid |
| New target tracks former ignored artifact path | Reject donor; never overwrite ignored file |
| Staged, dirty, conflicted or unknown untracked donor | Cannot mark Available/reassign; preserve content |
| Assigned detached worktree with new local commit | Cannot silently release/repurpose; reopening preserves HEAD |
| Interrupted checkout (after intent or mid-process) | Quarantined on restart; bytes retained; no automatic reset/retry/clean |
| Two concurrent protocol launches | Exactly one claim of an Available donor; other waits/reselects/falls back safely |
| Missing/externally moved worktree, wrong source origin, symlink/junction path | Clear failure/quarantine, no mutation of foreign path |
| Untrusted URL/branch, executable Git filter, unsafe Git config | Reject safely; no arbitrary command execution |
| Artifact cache symlinks / unsupported repo features | Defined supported behavior or fail closed, not silent traversal/copy |
| No safe warm candidate | Fresh isolated worktree; existing pool entries untouched |
| Editor fails after successful checkout | Workspace remains consistently Assigned |
| Existing legacy DB/profile present | New code refuses automatic overwrite/adoption |

For performance, collect **median and p95** of browser request→workspace ready and click→visible editor if measurable, plus tracked file writes and preserved artifact sizes, for fresh vs warm donor vs existing-feature reopen. Report actual distributions, not guessed percentages. A representative warm transition should show a meaningful improvement over fresh checkout; if not, investigate and simplify further.

## 12. Scope controls and agent instructions

- **Keep the current repo, C#, .NET/WinForms and Windows-oriented integrations.** No second application, generic daemon or new service.
- **Do not implement the old feature list.** Functionality must justify itself against browser speed, in-place reuse, artifact retention or safety.
- **No code execution from repository content** during browser-driven open (no build, restore, hooks, arbitrary filters or scripts).
- **No destructive operations on real existing directories or profiles.** Use disposable fixtures for transition/fault tests. Preserve legacy evidence.
- **Do not trade correctness for apparent speed.** An unsafe donor is ineligible; fallback is a fresh worktree or explicit error.
- **Do not overabstract.** One clear pool coordinator, one Git adapter, one small state store and small UI adapters should be the default.
- For each phase, show: modified files; why it directly serves the goal; tests run with pass/fail evidence; known safety gaps; before/after performance evidence when applicable.
- When a phase exposes an unresolved data-loss or code-execution possibility, stop that feature behind a fail-closed guard rather than declaring it complete.

### Final definition of done

From a browser click, opening an existing feature is a no-mutation reopen. Opening a new feature prefers the cheapest **safe, existing populated** worktree, updates tracked files **in place**, retains compatible cached artifacts, opens VS Code, and uses a fresh worktree only when no safe donor exists. The implementation is substantially smaller than the old snapshot/parking/slot lifecycle, and its latency improvement is demonstrated on real representative workloads.
