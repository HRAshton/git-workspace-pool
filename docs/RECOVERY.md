# Recovery without data deletion

Quarantined or interrupted worktrees are never automatically checked out, reset, cleaned, moved, adopted or deleted. Diagnostics displays the retained path and recorded HEAD. A Transitioning row is displayed as interrupted and is quarantined under the repository lock on the next operation.

Exit the tray before inspecting state. Back up the entire worktree, including ignored/untracked files and Git metadata, its source clone/object store, settings and `warm-v1/workspaces.db` and any companions. Inventory `git worktree list --porcelain`, HEAD, local refs, status and operation markers read-only. Do not infer success from a directory existing. Do not edit ownership rows to bypass checks. Preserve local commits with explicit user-reviewed Git recovery in the original object store before considering release.

The initial version deliberately has no automatic recovery/adoption command. Keep uncertain directories and use a new clean managed path for other work. Restoring an externally moved assignment or resolving an interrupted Git operation requires manual investigation after backup.

Legacy sessions.db, snapshots, released slots and parked worktrees belong to the archived implementation. See [legacy recovery](archive/RECOVERY.md) and [legacy lifecycle upgrade](archive/LIFECYCLE_UPGRADE.md). Never point the new registry at sessions.db.
