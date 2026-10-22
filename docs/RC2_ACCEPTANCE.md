# RC2 0.1.0-rc.2 — acceptance and safe upgrade checklist

**Scope:** Windows 11 x64, configured GitHub.com pull requests to local VS Code. This is an unsigned engineering candidate until all mandatory acceptance gates pass. Do not check off a test merely because a workflow began.

## Automated gates: required for the exact tested candidate source

- [ ] The normal PR Windows CI is green: restore, build, unit tests, fast real-Git E2E, release/slot crash tests, preview publish and per-user install smoke.
- [ ] The dedicated **RC2 Windows candidate** run is green: userscript syntax, clean release build, `Category=SlowE2E` recovery/crash tests, versioned ZIP, SHA-256 verification and isolated two-pass per-user installer smoke. The backup fixture must also pass checksum, non-overwrite and source-isolation tests.
- [ ] Record the tested Git SHA and run links, .NET SDK version, package SHA-256, test results, accepted waivers and downloadable artifacts. Compare the artifact's `RC2_BUILD_EVIDENCE.txt` to GitHub's actual candidate commit.
- [ ] Confirm all checks ran against the current RC2 head (not an outdated workflow after a push or a cancelled attempt).

## Installed browser, editor and data safety: manual Windows gates

- [ ] On a backed-up Windows 11 x64 profile with Git, VS Code and Tampermonkey, verify the ZIP SHA-256 against its adjacent `.sha256` using `Get-FileHash -Algorithm SHA256`.
- [ ] Extract, inspect and run `.\Install-PerUser.ps1` as a normal user. Confirm no elevation, a working tray launch, and registration of `prworkspace:` for that current user only.
- [ ] Configure a disposable trusted GitHub clone and workspace in `%APPDATA%\GitWorkspacePool\settings.json`. On a real GitHub PR click **Open locally**, accept the external protocol prompt and verify a correctly pinned linked worktree opens in **VS Code**.
- [ ] Click again to reopen the same path and preserve staged, unstaged, untracked and ignored files. A second PR must not steal an active worktree.
- [ ] Navigate GitHub via its in-page UI; verify the userscript button appears/disappears on the correct pages and that only canonical HTTPS PR URLs cross the protocol boundary.
- [ ] Snapshot and restore a disposable worktree with distinct staged/unstaged data and an ignored/untracked binary. Verify exact status and files in the separate recovery worktree; original work remains unchanged.
- [ ] Inject interruption, conflicting file occupant, link/junction, read-only file, locked file, low disk and unsafe Git config. Check recovery diagnostics while requiring no original user-file overwrites, resets, cleanup or arbitrary execution.
- [ ] Confirm quit/restart never triggers eviction/recycling, automatic worktree cleanup, background install, user project build or PR speculation. Advanced manual released-slot handoff and `node_modules` copying are not certified beyond their isolated tests.

## Configured storage-pressure advisories

- [ ] Configure and verify individual snapshot, recovered and combined byte warnings in the tray; test below, equal and above threshold while preserving all evidence.
- [ ] Confirm null/omitted warnings are disabled, invalid/nonpositive/out-of-range settings fail closed, and unsafe/unreadable directories are reported as unknown rather than freeable.
- [ ] Confirm no warning triggers deletion, automatic eviction, project build, release or workspace recycling.

## Read-only storage diagnostics

- [ ] Inspect the RC2 tray's **Local disk usage (read-only)...** against a disposable snapshot and side-by-side restore; confirm counts are plausible and clicking it never touches snapshot, recovery or active workspace contents.
- [ ] Under inaccessible/reparse-point or over-budget app-owned evidence, confirm it fails visibly rather than reporting zero freeable bytes or deleting files.
- [ ] Confirm logical-byte counts are not represented as physical disk savings or as permission to evict user data.

## Git and editor prerequisite inspection

- [ ] Run the packaged `Test-RC2Prerequisites.ps1` on the target Windows machine before installing production browser integration. Verify Git is at least 2.40.0, the exact configured VS Code `Code.exe` path exists, and missing Git/editor is reported clearly.
- [ ] Confirm the preflight does not open the editor, run Git in a repository, mutate user configuration, install dependencies, fetch remotes or change journal data.
- [ ] Complete the real tray/browser/editor integration test separately; presence checks are not proof that VS Code works.

## Support documentation shipped with RC2

- [ ] Inspect extracted `RECOVERY.md`, `RC2_RELEASE_NOTES.md` and `RC2_ACCEPTANCE.md`; run the backup helper on a stopped disposable profile and retain the verified manifest.
- [ ] Ensure operators can identify and preserve incomplete/unverifiable restore/parking/slot records without resetting, cleaning, force-moving or deleting worktrees.
- [ ] Validate a sanitized issue template contains finding kind and error only, not SQLite databases, settings, Git remote credentials, or private source code.

## Verified backup-first upgrade: required for RC2

1. Close the tray and confirm no Git Workspace Pool process remains running.
2. Extract RC2 ZIP and run the included Backup-PerUserState.ps1 with -BackupDirectory pointing at a NEW and separate trusted directory.
3. Check BACKUP_MANIFEST.json reports result=verified and has journal, optional WAL/SHM and settings SHA-256 records. Retain read-only.
4. Separately back up managed worktrees, parked worktrees and snapshots; the helper only protects the journal and settings.
5. Refuse migration if the backup cannot be verified. Partial backup evidence is retained for investigation.

- [ ] RC2 journal/settings backup and sidecar manifest verified on an actual stopped older installation, with separate workspace/snapshot backup.
- [ ] Refusal to overwrite an existing backup or use an overlapping/symlinked path or running tray checked on a real machine.
## Upgrade and rollback: manual gates

1. Exit the existing tray. Back up `%LOCALAPPDATA%\GitWorkspacePool\sessions.db` **and any SQLite `-wal`/`-shm` files together while closed**, `%APPDATA%\GitWorkspacePool\settings.json`, all managed worktrees, snapshots and parked directories. Do not delete recovery evidence.
2. On a disposable copy of an actual older preview user profile, install the RC2 package. Verify the previous binary directory, original worktrees, parked and recovery paths, snapshots and old settings remain intact after SQLite migrations.
3. Reinstall the same RC2 ZIP. Verify that the existing release is reused and data are untouched, including previously installed unknown files and current-user protocol ownership.
4. For rollback, stop RC2 and **restore the matching pre-upgrade database, journal sidecars and settings into a disposable profile** before running the older version. Never point the older binary at an RC2-migrated database.
5. Verify `.\Unregister-PerUser.ps1` refuses registrations outside this install root and removes only its own current-user protocol handler, preserving every versioned executable and all user files.

- [ ] Earlier preview -> RC2 journal migration and state preservation passed.
- [ ] Rollback from verified pre-upgrade backups passed; older executable never opened newer journal data.
- [ ] Protocol registration/unregistration and handling of an unrelated protocol owner passed.

## Signing, security, benchmarks and decision

- [ ] Verify real Windows Defender/AV/SmartScreen behavior for the unsigned ZIP, standard-user permissions and filesystem compatibility.
- [ ] Review open critical/high integrity/security bugs. Block RC2 promotion until they are fixed or the affected capability has an audited, enforced disablement.
- [ ] Record repeated baseline new-checkout vs owned-session-reopen time-to-visible-VS-Code (median and p95) and disk footprint. Do not claim performance multipliers based on fake-editor Git tests.
- [ ] Apply approved trusted code-signing for any public/signed RC2 distribution; otherwise approve **only private unsigned testing**, never label this artifact a signed release.
- [ ] Obtain release-owner acceptance, archive source commit + checksum + automated/manual gate results, then publish an immutable prerelease/release only for the exact certified commit.

**Promotion rule:** If any mandatory gate is unverified, the artifact is an **RC2 engineering candidate, not release-approved RC2**. An RC does not authorize automatic cleanup, eviction, user-data deletion, or adoption of unknown recovery worktrees.
