# Bitbucket Data Center 9 acceptance

The supported input is an exact HTTPS Git clone URL and short branch name. No pull request or REST API is required. This checklist separates offline engineering evidence from the checks that require your Windows machine, company authentication and browser.

## Install a verified build

1. Download the Windows preview ZIP and its `.sha256` from the same successful GitHub Actions run. Compare `Get-FileHash .\GitWorkspacePool-win-x64-preview.zip -Algorithm SHA256` with that checksum before extracting or running scripts. An unsigned checksum verifies integrity against that trusted run; it does not authenticate a publisher independently.
2. Extract to a new directory. Run `.\Test-PackageIntegrity.ps1`. Save `PACKAGE_MANIFEST.json` with your acceptance results. It identifies the exact source commit, source tree, SDK, workflow run and every packaged file hash. This is traceable packaging, not a claim that compressed archives are byte-for-byte reproducible.
3. Exit the previous tray. Run `.\Backup-PerUserState.ps1 -BackupDirectory C:\Backups\gwp-before-upgrade` with a new destination. Separately back up the source clone, managed worktrees, parked directories and snapshot store. The source clone includes Git objects, worktree registration and snapshot retention refs. A journal-only backup cannot restore those.
4. Run `.\Install-PerUser.ps1`. Installation stages a new version, verifies its full application inventory and launches a no-UI probe before changing the current-user protocol handler. Previous versions and user data are retained. No administrator rights are needed.
5. Start the installed tray executable shown by the installer. Use Settings to register a disposable repository and workspace root, and select your actual `Code.exe`. Run `.\Test-RC2Prerequisites.ps1` for read-only Git/editor prerequisites. This does not prove authentication or open VS Code.

## Company Git and browser checks

Use a disposable repository and files. Do not send passwords or private repository content in an issue or acceptance report.

Keep HTTPS certificate verification enabled. If the company uses a private CA, configure Git to trust that CA; remove generic or URL-scoped `sslVerify=false` settings. The app refuses those overrides before fetching, including URL-specific settings that would take precedence over a generic TLS override.

| Check | Expected result | Record locally |
|---|---|---|
| Run `git ls-remote origin` yourself from the source clone | Company-approved Git username/password or credential-helper login succeeds; no credentials in URL/settings | Git version; pass/fail |
| Settings, Check selected origin | Exact registered HTTPS clone URL is accepted; wrong repository is refused | pass/fail |
| Tray, Open Git branch | Correct detached branch commit opens in VS Code | Commit; elapsed time |
| Browser button with encoded repo + branch | Protocol consent, tray and VS Code complete the same flow | Browser; pass/fail |
| Branch containing `/` and Unicode | Exact branch resolves, without decoding twice | pass/fail |
| Unknown URL and invalid branch | Refused without creating a checkout | pass/fail |
| Stage one edit, make a second unstaged edit, add untracked and ignored files | Reopen and tray restart preserve all content and your local commits | Compare file hashes and `git status` |
| Remote branch advances | Existing owner reopens unchanged; a new session pins its fetched commit | Commit before/after |
| Cancel during fetch or checkout; restart | Durable ownership remains; retry or explicit recovery refuses ambiguous/dirty checkout without cleanup | Recovery state; file hashes |
| Snapshot and restore | New isolated restore destination matches captured staged/unstaged/untracked/ignored bytes; originals remain | Snapshot ID; hashes |
| Suspend, park, unpark, park and release | Retained worktree still opens, including user changes; slot can be explicitly selected for another branch | Paths; hashes |
| Guided reuse with compatible npm cache | Explicit selection only; optional copy succeeds or refuses safely; donor remains unchanged | Donor/destination hashes |
| No baseline names configured | No background fetch | pass/fail |
| Configure an exact baseline branch | Dedicated baseline ref updates; source/worktree files and checked-out HEAD stay unchanged | HEAD/status before/after |
| Storage warning threshold below observed bytes | Advisory warning; no files are removed | pass/fail |
| Locked file, unavailable server, denied directory or low free space in disposable environment | Clear failure/cancellation; evidence retained; no deletion or success claim | Error category; state |

Use the bundled `GIT_BRANCH_WORKSPACES.md` for the button contract and `RECOVERY.md` for inspection. The minimal userscript requires changing its placeholder `@match` to your exact host; it prompts for clone URL and branch. No Bitbucket DOM or API integration is assumed.

## Upgrade, rollback and measurements

Repeat the dirty-file/reopen checks with an actual previous profile backed up first. Do not open schema v9 with an older binary; old versions intentionally refuse it. To roll back, stop all tray processes, preserve the failed-upgrade state separately, restore the matching closed database/settings plus corresponding worktrees, source clone and snapshots from backup, then register the matching older version. Never replace only the database while leaving worktree ownership from another point in time.

Enable local operation timings in Settings only if wanted. Gather several completed opens, report median/p95, and separately time browser consent and visible editor readiness. Compare optional dependency copying against your normal install on a representative repository. Do not infer a universal speedup from synthetic tests or a single run. Disable automatic background work by clearing baseline names; guided reuse is opt-in. Automatic cleanup and eviction are not implemented.

Record the build source/tree, ZIP hash, Windows/Git/browser/VS Code versions, passed/failed checks and unresolved issues. Keep private paths/URLs and credentials out of shared evidence. Real Bitbucket DC 9 acceptance and signing with a release owner's certificate remain pending until performed in that environment. Optional provider PR adapters, extra artifact classes and ReFS acceleration are outside this repo/branch workflow.
