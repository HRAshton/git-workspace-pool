# Git Workspace Pool

A Windows tray application that opens GitHub pull requests in local VS Code worktrees. A Tampermonkey userscript supplies the browser action.

## Status

Experimental Windows preview. Configured local repositories create or reopen a dedicated Git worktree per PR, backed by a durable SQLite journal and pinned commit identity. Separately confirmed parking, release, released-slot checkout, recovery and compatible npm file copying are available. **Automatic** worktree recycling, eviction, dependency installation, project builds and deleting user files remain out of scope.

## RC2 engineering candidate (0.1.0-rc.2)

The [RC2 Windows candidate workflow](.github/workflows/rc2.yml) builds the next unsigned self-contained Windows x64 package after full real-process crash/recovery and package/backup smoke tests. RC2 adds the included [verified backup-first migration helper](docs/RC2_RELEASE_NOTES.md): stop the tray, run `Backup-PerUserState.ps1 -BackupDirectory <new-safe-location>`, and verify the copied SQLite journal, WAL/SHM files and settings. **Back up worktrees, parked worktrees and snapshots separately.** An actual older-profile migration, GUI acceptance, signed public release and safe rollback are still manual [RC2 acceptance gates](docs/RC2_ACCEPTANCE.md).

Run the bundled `Test-RC2Prerequisites.ps1` to check the supported Windows environment, Git >=2.40.0 and the **configured VS Code Code.exe path** without launching the app, changing user data or fetching any Git ref. This is a diagnostic, not a full installed UI acceptance test.

The extracted RC2 ZIP also bundles [safe recovery and support guidance](docs/RECOVERY.md) for read-only diagnostics, preservation of unfinished operations and credential-safe support requests.

## RC1 engineering candidate

A dedicated [RC1 Windows candidate workflow](.github/workflows/rc1.yml) builds an unsigned, self-contained Windows x64 package with app version `0.1.0-rc.1`, source provenance and a SHA-256 checksum. It also runs extended real-process recovery/crash tests. Download the `GitWorkspacePool-win-x64-rc1-candidate` artifact from a successful RC1 workflow run. This is **not** a signed GitHub Release.

[RC1 release notes](docs/RC1_RELEASE_NOTES.md) describe the supported GitHub PR → VS Code flow and deferred capabilities. [RC1 acceptance and upgrade/rollback](docs/RC1_ACCEPTANCE.md) lists mandatory manual Windows tests before approval as an actual release. Passing package CI alone does not certify browser/tray/editor integration, real prior-profile migration, security edge cases or code signing. Use verified backups of your workspaces and SQLite journal for RC1 evaluation.

## Windows preview ZIP (no .NET SDK required)

A PR Windows CI run produces a **self-contained, unsigned win-x64 preview ZIP** as a downloadable workflow artifact, plus a SHA-256 checksum. The installer only uses the current user's profile and does not need administrator rights.

1. Download the `GitWorkspacePool-win-x64-preview` artifact from a successful PR workflow run. Verify `GitWorkspacePool-win-x64-preview.zip` using the adjacent `.sha256` file and PowerShell `Get-FileHash -Algorithm SHA256`.
2. Extract the ZIP to a temporary folder. Review and run `.\Install-PerUser.ps1` from that folder. The installer copies the app into `%LOCALAPPDATA%\Programs\GitWorkspacePool\versions\<exe-hash>`, runs a no-UI startup smoke test, then registers the **current-user** `prworkspace:` protocol. It never starts the tray automatically.
3. Start the installed `GitWorkspacePool.Tray.exe` printed by the installer. It creates (if absent) `%APPDATA%\GitWorkspacePool\settings.json`; configure the trusted repository root there. Install the included `git-workspace-pool.user.js` into Tampermonkey. Git and VS Code must be installed and on the appropriate PATH.
4. When upgrading, **exit the old tray** before running the new ZIP's installer, then start the new path. The old version directory and all data stay in place for rollback. The new startup performs the existing conservative SQLite journal migration. Do not restore a newer SQLite journal with an older application without a backup.
5. To disable the protocol registration, run `.\Unregister-PerUser.ps1` from the extracted package, using the matching `-InstallRoot` if customized. It refuses to unregister a handler outside the install root and **does not delete installed binaries, user settings, snapshots, parked worktrees, or any other data**. Remove unused versioned binaries manually only after verifying backups and that the tray is not running.

**Distribution limitations:** The preview ZIP is unsigned and not an MSI/MSIX installer. Windows SmartScreen may flag unsigned executables. The automated packaging smoke test proves only that the self-contained executable launches, the upgrade-safe files can be staged twice, and existing files remain untouched. It does **not** run an actual Windows tray UI, browser userscript, protocol round-trip, VS Code, or AV/ACL/power-loss testing. CI artifacts are temporary and are not signed GitHub Release downloads.

For maintainers, the same package can be built with:

```powershell
.\scripts\Build-WindowsPackage.ps1 -OutputDirectory .\artifacts\preview-2026-10-22
.\scripts\Test-WindowsPackage.ps1 -PackagePath .\artifacts\preview-2026-10-22\GitWorkspacePool-win-x64-preview.zip -TestRoot .\artifacts\isolated-install-smoke
```

Both output and isolated smoke directories must be new. The smoke's `-SkipProtocolRegistration` path never changes HKCU registry associations.

## Getting started (Windows, .NET 10 SDK)

1. Build the solution: `dotnet build GitWorkspacePool.slnx`.
2. Start `GitWorkspacePool.Tray` once to create `%APPDATA%\GitWorkspacePool\settings.json` and register the per-user `prworkspace:` handler.
3. Configure a local repository and workspace root in `settings.json` using the documented example there.
4. Run the tray application and install `userscript/git-workspace-pool.user.js` in Tampermonkey.
5. Open a GitHub PR and click **Open locally**. The first request fetches that PR and creates a detached worktree. Subsequent requests reopen it without checkout, protecting local edits.

Session ownership data is stored in `%LOCALAPPDATA%\\GitWorkspacePool\\sessions.db`. The app can reconcile a previously interrupted checkout; it will **refuse to overwrite unknown or previously untracked existing worktrees**, including worktrees created by older preview builds. Manual adoption is not implemented yet. Back up existing preview workspaces before upgrading.

The configured clone's actual `origin` must identify the GitHub repository in the PR URL (HTTPS or SSH). A mismatched clone is rejected before the session journal or filesystem is changed.

No admin rights or background Windows service are required. The browser protocol is untrusted input: only recognized GitHub PR URLs matching configured repositories are accepted.

## Testing

The fast E2E suite uses a temporary local Git remote with GitHub-style PR refs, real Git worktrees, the versioned URL protocol, and real Windows Named Pipes. It uses a fake editor and **does not launch VS Code, the tray UI, or a browser**.

```powershell
dotnet test tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj --filter "Category!=FastE2E"
dotnet test tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj --filter "Category=FastE2E"
```

These tests run on every PR in Windows CI. The fast suite also launches **two real tray processes** in a disposable IPC probe mode to check mutex ownership, protocol forwarding and a missing pipe listener. That mode does not start the UI, open settings or register HKCU protocols; build the full solution before running the standalone tests locally. [Roadmap and acceptance gates](ROADMAP.md) distinguish this from the later installed-browser-and-editor smoke test.

## Explicit session suspension

The tray menu provides **Suspend active PR (keep worktree)...**. This only changes the durable activity state: the original Git worktree, all local modifications and exclusive PR ownership stay untouched. Opening the same PR resumes it. **Suspension does not free disk space, release a warm slot or enable recycling.** A worktree in `RecoveryRequired` can no longer be reactivated by normal PR opening.

## Initial non-destructive worktree parking

The tray offers **Park PR worktree (move, no deletion)...** and **Unpark PR worktree...**. Parking uses `git worktree move` to relocate the complete linked worktree to `<workspace-root>/.gwp-parked/<session-id>`, preserving staged, unstaged, untracked and ignored files. A durable SQLite intent precedes every move and interrupted moves are retried only after checking source/destination registration. Unparking rejects an occupied original path. This is not automatic release, eviction or warm-pool reuse.

## Explicit release and reuse of an empty workspace slot (opt-in)

The tray supports a **manual**, non-destructive cross-PR slot handoff:

1. **Park PR worktree (move, no deletion)...** to move the entire Git linked worktree into private parking. All staged, unstaged, ignored and untracked files stay in the parked directory.
2. **Release parked PR slot (keep user files)...** to relinquish the PR's *former* workspace path. Read the confirmation: once released, that PR cannot unpark back into the original slot. Its parked worktree and published snapshots remain preserved. If the release is interrupted, inspect recovery diagnostics; **Cancel pending slot release...** can cancel an uncommitted preparation.
3. **Open PR in a released slot...** and select a listed Ready slot for a configured repository. Enter the new GitHub HTTPS pull-request URL and confirm. The app rechecks trusted Git origin, slot identity and generation, refuses occupied/unregistered/unsafe destinations, and creates a fresh Git worktree with a new exclusive PR lease. The editor opens only after successful checkout.
4. If allocation is interrupted, review **Recovery diagnostics...**, then choose **Finish interrupted PR checkout (verified only)...** for the exact configured PR, inspect its path, generation and pinned Git commit, and explicitly confirm. Recovery promotes only a still-matching Reserved lease with an already existing, registered, **completely clean** checkout at the pinned commit, then opens VS Code. No checkout, fetch, reset, repair, deletion or automatic cleanup is performed. Missing, unpinned, dirty, altered or stale reservations remain Reserved/Preparing with all their files retained for manual inspection. The normal PR opener never silently reassigns them.

Opening the original released PR's GitHub URL through the browser button also reopens its verified retained parked worktree directly in VS Code, after checking the configured repository origin. The URL opener never reallocates the old slot, creates a worktree or moves parked files.

The tray also provides **Open retained released PR (without moving files)...**. Select the original released PR to verify its preserved `.gwp-parked` Git worktree and open it directly in VS Code, even after another PR has taken its former slot. This does not unpark or reclaim the original path, change leases, restore snapshots, or move/delete files. You may continue editing and committing in the retained worktree; if its Git registration or path cannot be verified, the editor is not launched.

This reuses the **former directory/slot**, not cached build outputs or `node_modules`. It does not automatically choose slots, reclaim disk, delete user data or predict PRs. The released PR remains parked under its original session identity. An actual speed benefit and artifact-preserving warm reuse are still separate milestones.

## Experimental npm artifact reuse (opt-in)

The tray now exposes **Reuse compatible npm dependencies (copy only)...** after manually parking/releasing PR A and opening PR B in its former slot. Select the active PR/donor pair and confirm. The adapter:

- Requires both PRs to use the same trusted Git repository and original slot, with the old PR in `Released` and the new PR `Active`, and verifies both Git worktree registrations and pinned commits under OS locks.
- Requires identical `package.json` and `package-lock.json` files; transfers **only** ignored, untracked `node_modules` regular files. Both Git check-ignore rules must cover the cache path.
- Refuses symlinks/junctions, destination collisions, tracked paths, more than 20,000 files or 512 MiB, and unsupported filesystem states. Uses independent copies and `CreateNew`; **never deletes or moves source files**.
- Reports file count, bytes and transfer duration. An interrupted copy leaves its incomplete destination for inspection; a retry will not overwrite it. No rollback, automatic cleanup or cross-branch dependency execution is attempted.

This is an **experimental manual feature**, not automatically selected warm reuse. Compatibility is strict and limited to npm; it makes no claim that file transfer is faster than dependency installation. To collect reproducible local timings, run the offline synthetic benchmark via `dotnet test tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj --filter "FullyQualifiedName~WarmNpm_BenchmarkColdCheckoutVsVerifiedArtifactCopy" --logger "console;verbosity=detailed"`. Timings are environment-specific and exclude `npm install`, VS Code startup and web-to-tray latency.

## Opt-in Windows performance measurements

Run the **Manual reuse benchmark** workflow from GitHub Actions when you want repeatable Windows-runner evidence without consuming CI time on every PR. The workflow is **manual-only**, produces a downloadable `manual-warm-reuse-results` JSON/log artifact, and takes 3, 5, or 10 samples. It uses temporary offline Git fixture repositories, real SQLite ownership transitions and the **production verified npm artifact copier** with 1,024 synthetic files. It does not install npm packages or launch VS Code.

For reference hardware, PowerShell 7, .NET 10, Git and optionally Node/npm:

```powershell
.\scripts\Benchmark-WarmReuse.ps1 -OutputDirectory "$env:TEMP\gwp-git-benchmark-$(Get-Date -Format yyyyMMdd-HHmmss)" -Iterations 5
```

Add `-NpmProjectPath 'C:\src\trusted-npm-project'` for **a second, separate comparison** of warmed-cache `npm ci --offline --ignore-scripts` versus ordinary `node_modules` file copying using a trusted, standalone project with `package-lock.json`. The benchmark copies only manifests and never runs npm lifecycle scripts or touches the supplied project's `node_modules`. Its initial disposable donor install may access the package registry to prime an isolated cache; later timed installs run offline. Local file/Git dependencies, npm workspaces and symlinked payloads are refused.

The runner saves every raw timing sample, p50/p95 (nearest rank), file count, logical copied bytes and caveats in `benchmark-results.json`. It refuses existing output directories and **does not automatically clean up** the output folder or its npm trial directories (the Git E2E fixture cleans up its own disposable worktrees). Inspect the output and delete it manually when finished; large npm projects consume extra disk space.

**Interpretation:** A reused *empty slot* still needs a fresh Git checkout. The standalone npm baseline measures ordinary file copy, not the app's integrity-verified copying; compare it alongside, not as if the two copy implementations are identical. Git fixture timings use a fake editor. Neither benchmark proves actual browser-click-to-visible-editor latency, lifecycle-script/install equivalence, physical NTFS disk savings or a user-visible speedup. Installed Windows browser/VS Code measurements on representative projects remain a release gate.

## Experimental workspace snapshots

The tray menu provides **Snapshot active PR...** for an explicit, non-destructive snapshot. Captures include the Git index, staged blobs independently from unstaged working files, tracked changes, untracked and ignored files (including binary data), deletions and a SHA-256 manifest. Snapshots are stored under `%LOCALAPPDATA%\\GitWorkspacePool\\snapshots`. Only explicitly selected active sessions are eligible.

A snapshot has a conservative **50,000-file / 2 GiB** budget and refuses symlinks/junctions. It does not clean, release, rename, or recycle a worktree. Incomplete snapshots remain marked `.pending-*` for inspection; a published snapshot remains intact if journal registration fails. Use **Restore snapshot to new worktree...** to reconstruct an isolated worktree under `%LOCALAPPDATA%\\GitWorkspacePool\\recovered`. The restore checks the manifest, file hashes and original Git index, then verifies that the recovered Git status matches the saved status. It never writes to or switches the original PR session.

**Limitations:** recovery worktrees are deliberately outside the managed pool and are not automatically adopted or cleaned. Interrupted restores may leave a separate worktree for inspection. Snapshots still do not authorize automatic release, move, cleanup or recycling until the remaining fault-injection and recovery cases are implemented.

Use **Recovery diagnostics...** to inspect interrupted snapshots, missing journalled data, and isolated recovery worktrees. This is **read-only** and does not reclaim storage. See the [safe recovery runbook](docs/RECOVERY.md).

## Non-goals

The app never runs build, npm install, dotnet restore or untrusted PR scripts. Worktree recycling, automatic snapshot adoption, artifact policies, GitLab/Bitbucket adapters and intelligent selection are planned later; **do not use this preview to auto-clean or reassign worktrees**.

## Engineering principles

C# style follows [WinCfApiWrapper](https://github.com/HRAshton/WinCfApiWrapper): CSharpier formatting, StyleCop analyzers, file-scoped namespaces, explicit types unless evident, and `_field` naming. See [architecture](docs/ARCHITECTURE.md).
