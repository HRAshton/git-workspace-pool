> Historical documentation for [the archived implementation](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite/docs). It does not describe the new warm-worktree engine.

# RC1 0.1.0-rc.1 — acceptance and safe upgrade checklist

**Scope:** Windows 11 x64, configured GitHub.com pull requests to local VS Code. This is an unsigned engineering candidate until all mandatory acceptance gates pass. Do not check off a test merely because a workflow began.

## Automated gates: required for the exact tested candidate source

- [ ] The normal PR Windows CI is green: restore, build, unit tests, fast real-Git E2E, release/slot crash tests, preview publish and per-user install smoke.
- [ ] The dedicated **RC1 Windows candidate** run is green: userscript syntax, clean release build, `Category=SlowE2E` recovery/crash tests, versioned ZIP, SHA-256 verification and isolated two-pass per-user installer smoke.
- [ ] Record the tested Git SHA and run links, .NET SDK version, package SHA-256, test results, accepted waivers and downloadable artifacts. Compare the artifact's `RC1_BUILD_EVIDENCE.txt` to GitHub's actual candidate commit.
- [ ] Confirm all checks ran against the current RC1 head (not an outdated workflow after a push or a cancelled attempt).

## Installed browser, editor and data safety: manual Windows gates

- [ ] On a backed-up Windows 11 x64 profile with Git, VS Code and Tampermonkey, verify the ZIP SHA-256 against its adjacent `.sha256` using `Get-FileHash -Algorithm SHA256`.
- [ ] Extract, inspect and run `.\Install-PerUser.ps1` as a normal user. Confirm no elevation, a working tray launch, and registration of `prworkspace:` for that current user only.
- [ ] Configure a disposable trusted GitHub clone and workspace in `%APPDATA%\GitWorkspacePool\settings.json`. On a real GitHub PR click **Open locally**, accept the external protocol prompt and verify a correctly pinned linked worktree opens in **VS Code**.
- [ ] Click again to reopen the same path and preserve staged, unstaged, untracked and ignored files. A second PR must not steal an active worktree.
- [ ] Navigate GitHub via its in-page UI; verify the userscript button appears/disappears on the correct pages and that only canonical HTTPS PR URLs cross the protocol boundary.
- [ ] Snapshot and restore a disposable worktree with distinct staged/unstaged data and an ignored/untracked binary. Verify exact status and files in the separate recovery worktree; original work remains unchanged.
- [ ] Inject interruption, conflicting file occupant, link/junction, read-only file, locked file, low disk and unsafe Git config. Check recovery diagnostics while requiring no original user-file overwrites, resets, cleanup or arbitrary execution.
- [ ] Confirm quit/restart never triggers eviction/recycling, automatic worktree cleanup, background install, user project build or PR speculation. Advanced manual released-slot handoff and `node_modules` copying are not certified beyond their isolated tests.

## Upgrade and rollback: manual gates

1. Exit the existing tray. Back up `%LOCALAPPDATA%\GitWorkspacePool\sessions.db` **and any SQLite `-wal`/`-shm` files together while closed**, `%APPDATA%\GitWorkspacePool\settings.json`, all managed worktrees, snapshots and parked directories. Do not delete recovery evidence.
2. On a disposable copy of an actual older preview user profile, install the RC1 package. Verify the previous binary directory, original worktrees, parked and recovery paths, snapshots and old settings remain intact after SQLite migrations.
3. Reinstall the same RC1 ZIP. Verify that the existing release is reused and data are untouched, including previously installed unknown files and current-user protocol ownership.
4. For rollback, stop RC1 and **restore the matching pre-upgrade database, journal sidecars and settings into a disposable profile** before running the older version. Never point the older binary at an RC1-migrated database.
5. Verify `.\Unregister-PerUser.ps1` refuses registrations outside this install root and removes only its own current-user protocol handler, preserving every versioned executable and all user files.

- [ ] Earlier preview -> RC1 journal migration and state preservation passed.
- [ ] Rollback from verified pre-upgrade backups passed; older executable never opened newer journal data.
- [ ] Protocol registration/unregistration and handling of an unrelated protocol owner passed.

## Signing, security, benchmarks and decision

- [ ] Verify real Windows Defender/AV/SmartScreen behavior for the unsigned ZIP, standard-user permissions and filesystem compatibility.
- [ ] Review open critical/high integrity/security bugs. Block RC1 promotion until they are fixed or the affected capability has an audited, enforced disablement.
- [ ] Record repeated baseline new-checkout vs owned-session-reopen time-to-visible-VS-Code (median and p95) and disk footprint. Do not claim performance multipliers based on fake-editor Git tests.
- [ ] Apply approved trusted code-signing for any public/signed RC1 distribution; otherwise approve **only private unsigned testing**, never label this artifact a signed release.
- [ ] Obtain release-owner acceptance, archive source commit + checksum + automated/manual gate results, then publish an immutable prerelease/release only for the exact certified commit.

**Promotion rule:** If any mandatory gate is unverified, the artifact is an **RC1 engineering candidate, not release-approved RC1**. An RC does not authorize automatic cleanup, eviction, user-data deletion, or adoption of unknown recovery worktrees.
