# In-place rewrite evidence

## Preserved source

`archive/pre-in-place-rewrite` preserves commit `cf0bc2832bfb01257837909ee29350b5dcb401aa` and the complete previous implementation. It does not back up runtime data.

## Inventory and backup before changing installed profiles

Exit the tray and VS Code before taking a consistent backup. Do not run disposable fixture tests against a real source clone or worktree.

Old settings: `%APPDATA%/GitWorkspacePool/settings.json`. Old state: `%LOCALAPPDATA%/GitWorkspacePool/sessions.db` plus any `-wal`/`-shm` companions. Snapshot payloads and restored checkouts live below `snapshots` and `recovered` in that state directory. Source clones, workspace roots and parked directories can be elsewhere: inventory every configured path and the paths in the legacy journal before proceeding.

Use the archived `scripts/Backup-PerUserState.ps1` with a new destination outside the live profile for verified settings/journal copies. That script does not back up worktrees, source clones or snapshots. Back those up separately with a tool that preserves ignored/untracked files, hidden Git metadata and link identities without following links. Never overwrite the originals. Record `git -C <source> worktree list --porcelain` and `git -C <source> for-each-ref refs/git-workspace-pool/` as read-only inventories; do not prune refs or registrations. A legacy SQLite inventory must use read-only mode and must not upgrade its schema. Keep the archived recovery documentation for interpreting old states.

The new ownership engine uses a distinct `warm-v1/workspaces.db` file and refuses to initialize a foreign schema. It never imports sessions.db or adopts paths merely because they exist. Use a new workspace root for a clean profile. Unknown occupied directories remain untouched.

## Validation and measurement ledger

The baseline SDK is pinned by global.json to 10.0.402. The development host is Linux and initially has no .NET SDK or Windows desktop. Windows-only build, UI, browser-to-visible-editor and installer results must come from Windows CI or an explicitly recorded target-machine run. Do not label Linux tests as Windows acceptance.

The previous main attestation for `5bc030a843f1e6cf7450321de544c4b42a592e5b` succeeded. That is historical evidence, not validation of this rewrite. No representative real-repository latency measurements have yet been supplied. Synthetic fixture measurements must be labeled synthetic. Median/p95 browser-to-editor and real workload improvement remain acceptance gates until measured, not inferred from a diff heuristic.

Automatic validation runs on PRs only. Attestation is manual on trusted main when a distributable candidate is actually needed. Each final PR head runs the needed lanes once; no push-triggered repeat after merge.

## Representative Git-only experiment (Linux)

A separately cloned disposable `dotnet/roslyn` bare repository was benchmarked with `scripts/Benchmark-InPlaceGit.py`. No project code, restore or build ran. The seed was read-only; all checkout mutations were in newly created fixtures. The full raw samples, pinned baseline/target commits, Git version and platform are in [roslyn-git-checkout-linux.json](evidence/roslyn-git-checkout-linux.json).

35,440 tracked target files; 58 changed tree paths; five samples; a 16 MiB ignored artifact retained in the exact same physical directory/inode every time. Fresh Git checkout median/p95: 6,538/6,726 ms. In-place Git checkout median/p95: 77/102 ms. 56 target files received new timestamps in each warm sample (the tree delta also includes two deleted paths); the artifact survived unchanged. These are Git-command durations, excluding application safety prechecks, .NET/SQLite, fetch/network, browser and editor. They demonstrate the filesystem mechanism on one real repository, not a Windows end-to-end speedup.

Windows CI at the second revision built successfully and passed 80/81 tests, including all three real process-kill boundaries (after intent, during actual Git checkout with index.lock retained, and after checkout before ownership publication), a separate-process repository lock, and ignored cache junction handling. The remaining failure exposed a connection-disposal bug on rejecting a foreign database; corrected before the final run.

The shortlist preflight now inspects filesystem paths that actually change between donor and target, with ancestor checks cached within that inspection. It still validates complete tree metadata for unsupported paths/modes, tracked/index cleanliness and registration. It does not recursively scan artifacts or touch unchanged tracked files to estimate cost.

The next Windows revision passed all 81 tests and the real application launch/install/idempotent upgrade/data-preservation smoke. A remaining negative packaging fixture still referenced an archived flat document name; it now tampers with the current RECOVERY.md instead. Final review also restored explicit tests for dedicated fetch refs/FETCH_HEAD preservation, bare sources, unsafe transport configuration and case-sensitive LFS filter names, and made locked/prunable/ambiguous worktree registrations ineligible for reassignment.
