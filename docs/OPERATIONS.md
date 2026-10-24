# Settings, cancellation and local diagnostics

## Repository and editor setup

Open **Settings...** in the tray. The **Git repositories** tab accepts a credential-free HTTPS clone URL, existing local clone folder and separate workspace root. The optional **GitHub PR repositories** tab preserves the original PR workflow. Use **Browse selected folder...** for a path cell. **Check selected origin** runs a read-only Git origin/configuration check, with a 20-second deadline; it does not fetch or modify a worktree.

Authentication remains in Git's trusted named credential helper. No password field or provider API login is added. Configure Git Credential Manager and complete `git ls-remote origin` from your own terminal first.

The editor tab accepts an existing VS Code executable path; blank selects the standard current-user installation. Snapshot byte/entry limits cannot exceed the existing tested ceilings. Limits apply after restarting the tray. New repository/editor settings and operation controls are read for future operations; existing worktrees are never moved by editing their settings. Changing a source/root for an existing owner is refused when it is reopened; it is not an automatic migration.

New profiles start with empty repository lists, rather than an unusable example clone. Existing JSON settings remain supported. Unknown top-level settings are preserved when saving through the window.

## Settings publication and recovery

Saving validates identities, duplicates, absolute/disjoint paths, existing source folders and the editor path. The live settings file is never truncated in place. A complete new file is flushed, then atomically replaces the live file while retaining `settings.json.previous`. A fingerprint rejects edits made since the window was loaded. The application lock serializes its own writers; external editors do not participate in that lock. Keep the retained previous file when resolving an external-edit race.

A failed publication retains its `.pending-*` file for inspection. Oversized, linked or inaccessible settings fail closed. **Reload** explicitly discards unsaved window edits after confirmation. Keep closed-profile backups for journal upgrades; the previous settings file is not a database backup.

## Cancel and timeouts

Opening, snapshot/restore, parking, release, released-slot allocation/recovery and artifact transfer show an operation window. **Cancel** (or closing that window) requests cancellation and waits for the operation to stop safely. The configured timeout defaults to 15 minutes and may be set from 1 to 120 minutes. Git subprocesses are terminated on cancellation; copy/export services retain their existing cancellation and source-preservation behavior.

Cancellation is not rollback. Pending snapshots, partial dependency copies, parking/release intents and Reserved checkouts remain owned and visible in recovery diagnostics. Do not remove them just because the progress window closed. Reopen or use the explicit verified recovery/cancel-release commands appropriate to that state.

## Opt-in local timings

Recording is disabled by default. Enable **Record local operation timings** in Settings. Records under `%LOCALAPPDATA%\GitWorkspacePool\diagnostics\timings.jsonl` contain only UTC timestamp, a fixed operation category, elapsed milliseconds and a fixed outcome. No URL, path, branch, error text, source code or credential is a timing field. Nothing is transmitted.

The current log is bounded to 1 MiB; the previous diagnostic log is retained during rotation. This rotation affects only opt-in timing diagnostics, never snapshots, journal evidence or worktrees. Recording failures do not change the result of the workspace operation.

**Local operation timings...** shows completed/cancelled/failed sample counts and nearest-rank median/p95 values for completed samples. Empty groups show `n/a`; malformed records are counted rather than silently reported as measurements. The report reads the current bounded log.

These durations end when the application operation returns. They exclude browser protocol prompts and do not prove VS Code is visibly ready. They are diagnostic evidence, not a demonstrated warm-pool speedup. Installed reference measurements remain a separate acceptance gate.
