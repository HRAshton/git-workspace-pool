# Shared branch lifecycle upgrade (journal v9)

This version connects generic Git branches to the existing workspace pool operations. It does not require Bitbucket APIs or change the repo/branch browser protocol.

## Before installing

1. Exit the tray.
2. Run the bundled `Backup-PerUserState.ps1` into a new location and verify its manifest. It covers settings and the complete SQLite database/sidecars, including generic branch ownership.
3. Back up source clones, managed/parked worktrees and snapshots separately.
4. Install the new verified Windows ZIP, then start its tray executable.

## What changes

The first start transactionally upgrades `sessions.db` to schema 9. Existing PR sessions, snapshots, restore records, events and leases remain intact. Each legacy generic branch keeps its original operation ID, original workspace path, state and pinned commit. A protected lease is added to the shared slot registry. The old branch table remains as migration evidence; the shared journal is authoritative afterward. No worktree files are moved, cleaned or checked out during migration. Conflicting path ownership or malformed data fails closed and rolls back the branch migration.

Branch names remain case-sensitive. A durable internal subject number indexes the shared journal; it is not a PR number and the tray displays clone URL and branch instead.

## Available branch operations

- Snapshot/side-by-side restore, including distinct staged/unstaged, untracked and ignored content.
- Suspend/resume without releasing ownership or modifying files.
- Explicit park/unpark with the existing Git-aware move journal.
- Explicit parked-slot release, retaining original files and snapshots.
- Open another branch in the released slot through generation-checked allocation.
- Reopen the released branch's retained parked worktree even after its former slot has a different owner.
- Explicit compatible npm dependency transfer under the existing strict compatibility and copy-only policy.
- Read-only interrupted-operation inventory and explicit verified recovery of a Reserved slot.

Automatic deletion or recycling is not enabled by this migration. Existing data and unfinished operations remain protected.

## Rollback

Older applications reject schema 9. To evaluate rollback, stop the tray and restore the matching closed pre-upgrade database, sidecars and settings into a disposable backed-up profile before starting the old version. Never lower `PRAGMA user_version` or point an old application at a newer live journal. New sessions/snapshots made after the backup require separate preservation.

## Validation boundary

Automated tests cover legacy import, operation/path/state preservation, retained PR snapshot references, branch snapshot/restore, suspension, parking, released-slot reuse, preserved donor files, compatible dependency transfer, and explicit interrupted allocation recovery. Installed Windows acceptance and rollback on a real older user profile still require recorded evidence from that environment.
