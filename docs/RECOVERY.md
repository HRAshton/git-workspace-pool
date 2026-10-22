# Recovery diagnostics and safe manual triage

The **Recovery diagnostics...** tray menu performs a **read-only** inventory of the application's snapshot directory, published SQLite snapshot receipts, and isolated recovered worktrees. It does **not** repair files, recover a lost receipt, remove directories, evict a session, or adopt any worktree into the warm pool.

This feature is for finding artifacts left behind by a process kill, Windows restart, Git failure, or interruption between filesystem and SQLite operations.

## Storage locations

- Journal: `%LOCALAPPDATA%\\GitWorkspacePool\\sessions.db`
- Pending and published snapshots: `%LOCALAPPDATA%\\GitWorkspacePool\\snapshots\\<operation-id>\\`
- Side-by-side recovered worktrees: `%LOCALAPPDATA%\\GitWorkspacePool\\recovered\\`
- Per-workspace operation locks: `%LOCALAPPDATA%\\GitWorkspacePool\\locks\\`

The recovery scanner does not recursively inspect arbitrary user directories. It recognizes only these app-owned roots, uses bounded scans and refuses to follow junctions/symlinks.

## Findings

| Kind | Meaning | Safe response |
| --- | --- | --- |
| `IncompleteSnapshot` | A `.pending-<snapshot-id>` directory was left before atomic publication. | Keep it for investigation; do not assume it is complete or restorable. |
| `UnjournalledSnapshot` | `snapshot-<snapshot-id>` exists, but its ownership/receipt is not committed in SQLite. | Preserve the directory and journal; normal restore refuses unjournalled snapshots. |
| `MissingSnapshot` | SQLite has a receipt, but its expected directory is absent or not recognized. | Verify storage volume/path and backups; do not remove the journal entry. |
| `UnmanagedRecoveryWorktree` | A legacy `restore-...` worktree exists without a restore journal entry. | Inspect its files and Git state. Never assume it is disposable. |
| `InterruptedRestore` | SQLite recorded restore intent, but final verification was never committed. | Preserve and inspect; restoring again creates a separate new path. |
| `VerifiedRecoveryWorktree` | Restore verification succeeded, but the worktree is not part of the PR pool. | Preserve it until reviewed; no automatic reclamation. |
| `MissingRecoveryWorktree` | Restore intent exists, but its target directory is missing. | Inspect Git metadata and storage without deleting the journal record. |
| `InterruptedParking` | A parking move has an intent but no committed completion. | Use Park to reconcile the two registered locations without deleting files. |
| `InterruptedUnparking` | A return move has an intent but no committed completion. | Use Unpark only when the original destination is unoccupied. |
| `MissingParkedWorktree` | A parked worktree is missing or not registered by Git. | Investigate the original clone; never delete its journal entry. |
| `UnverifiableParkedWorktree` | Git registration for a parked worktree could not be confirmed. | Leave all paths intact and check source clone availability. |
| `SlotOwnershipInconsistent` | Protected/Ready/Reserved slot state disagrees with its exclusive lease, generation or PR owner. | Stop release/recycling attempts; preserve SQLite and all worktree files for inspection. |
| `PendingSlotReleasePreparation` | A release preparation was journalled and continues to protect a slot; it does not mean release is complete. | Preserve the parked tree and lease. Cancel the preparation explicitly before Unpark; never infer slot availability. |
| `UnregisteredRecoveryDirectory` | A recovery directory exists without a corresponding Git worktree registration. | Inspect manually. Do not assume partial checkout means it is safe to delete. |
| `UnverifiableRecoveryDirectory` | Git could not establish registration. | Check the original clone is accessible and retry diagnostics; preserve data. |
| `UnknownEntry` | Unexpected name/file or a directory whose owner cannot be proven. | Do not delete; inspect ownership. |
| `UnsafeReparsePoint` | A junction or symlink was found. | The scanner does not follow it. Resolve the path/security concern manually. |

Only the most recent 25 findings are shown in the tray menu; scanning still examines all entries under a protective count limit. The path and description appear in the item tooltip. A scanner error indicates **unknown safety**, not a clean state.

## Safe investigation procedure

1. Close the tray app and any affected editor windows before making backups. Do not delete or move any worktree.
2. Back up the entire `GitWorkspacePool` local state directory **and** the relevant source clone and worktree folders to a separate location. SQLite journal, Git metadata and working files must be preserved together.
3. Identify the original source repository registered in `settings.json`.
4. Use the read-only Git command `git -C "<source-clone>" worktree list --porcelain` to compare registrations with the recovery findings.
5. For a **journalled** snapshot, use **Restore snapshot to new worktree...** and verify the recovered files and Git status. The source session is deliberately not modified.
6. For an **unjournalled** or **incomplete** snapshot, do not rely on automated restoration: keep the directory and investigate the manifest and original worktree.
7. If uncertain, leave everything intact and record the diagnostic finding and relevant Git error. Storage reclamation and orphan adoption are not implemented yet.

**Forbidden shortcuts:** `git clean -fdx`, `git reset --hard`, manual deletion of `.git/worktrees`, blindly moving linked worktrees, deleting snapshots to silence diagnostics, or treating a successful file copy as proof of safe user-data removal.

## Limitations before RC1

Recovery inspection alone is **not** an operation journal for the restore lifecycle. Concurrent filesystem modifications, disk exhaustion, reparse-point races and unsupported Git features still require further hardening. No automatic release, recycle, eviction or snapshot pruning is enabled by this functionality.
