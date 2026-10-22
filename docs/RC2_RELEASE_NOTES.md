# Git Workspace Pool 0.1.0-rc.2 — engineering release candidate

**Platform:** Windows 11 x64. **Distribution:** unsigned, self-contained ZIP. **Status:** available for acceptance testing; not approved as a signed public release.

## RC2-supported workflow

- Install into the current user's profile without administrator rights; smoke-check staged executable before publishing the installed version and registering the current-user protocol.
- Install the included Tampermonkey userscript, configure a trusted local GitHub.com clone and workspace root, then use **Open locally** on a GitHub pull request to open its commit-pinned Git worktree in VS Code.
- Reopen the same PR without resetting staged, unstaged, untracked or ignored data. SQLite journal keeps exclusive session ownership.
- Use the explicitly confirmed suspend, park/unpark, snapshot, side-by-side restore and read-only recovery diagnostics operations. Advanced manual slot-release and artifact-copy commands are experimental, not yet certified for general RC2 use.
- Automatic worktree recycling, eviction, deletion, cleanup, speculative PR prediction, background baseline fetching and building/restoring dependencies from user projects remain disabled or unimplemented.

## New in RC2: verified backup-first migration

- RC2 includes a Backup-PerUserState.ps1 helper; exit the tray and choose a **new backup location outside the live profile**.
- It backs up sessions.db, any existing -wal/-shm SQLite sidecars and settings.json, checks all copied SHA-256 digests and source stability, and writes BACKUP_MANIFEST.json.
- Backup refuses running tray processes, existing destinations, overlapping backup/source paths and reparse points; partial evidence is retained on failure.
- **Separately back up all worktrees, parked directories and snapshots.** The helper covers only SQLite state and settings, not a full user-data rollback.
- The RC2 workflow tests backed-up synthetic prior state and checksum/non-overwrite enforcement; actual older-profile migration and GUI rollback still require manual Windows acceptance.

### Backup command (PowerShell; only after exiting tray)

Run the included Backup-PerUserState.ps1 with -BackupDirectory pointed at a new directory on trusted storage. See RC2_ACCEPTANCE.md for the operator procedure.
## Bundled support and recovery runbook

RC2 includes `RECOVERY.md` directly in the extracted ZIP. It explains the supported recovery finding types, backup-first triage, read-only Git worktree inspection and what evidence may be shared safely without leaking credentials or user code. RC2 ZIP smoke refuses packages missing this runbook or the matching RC2 acceptance/release notes.

## Build and verification

The RC2 Windows workflow produces `GitWorkspacePool-win-x64-0.1.0-rc.2.zip` with its adjacent `.sha256`, `RC2_BUILD_EVIDENCE.txt` recording exact source commit and hash, and this release note. It uses .NET 10, `win-x64`, self-contained single-file publish, version `0.1.0-rc.2`, and dedicated no-UI installer smoke. The workflow runs extended real-process snapshot/recovery crash E2E. These checks are **not code-signing, attestations, GUI acceptance or upgrade-from-an-older-version proof**.

## Limits and excluded release features

- GitHub.com PRs with matching configured `origin` and local VS Code are supported; enterprise provider remote matching, GitLab, Bitbucket, Azure DevOps and fork-specific PR fetch strategies are not certified.
- The ZIP is unsigned; no MSI/MSIX, signing certificate, auto-updater or published GitHub Release is supplied by this candidate workflow.
- Headless CI does not prove the installed tray, real browser/Tampermonkey, protocol prompt, VS Code editor, Windows Defender, anti-virus locks or all filesystem race cases.
- Database changes may require forward SQLite migrations; do not open a newer migrated journal with an older binary. Back up the journal, settings, snapshots and worktrees before installing.
- Open filesystem alias/reparse, deletion-replacement, named credential helper and real disk-full edge cases prohibit unattended release or cleanup.

## Release approval

Complete every required item in [RC2 acceptance](RC2_ACCEPTANCE.md), including manual Windows, security, older-install migration, rollback, benchmark and signing gates, before calling this a release-approved RC2. The full [roadmap](../ROADMAP.md) includes broader features explicitly deferred from this candidate.
