# Git Workspace Pool

A Windows tray app that opens a **Git repository + branch** in an owned VS Code worktree. Works with HTTPS and SSH remotes, including Bitbucket Data Center. No provider API, pull request URL or provider-specific fetch refs are required.

## Open a repository

1. Install Git and VS Code. Authenticate in your terminal first: `git ls-remote <clone-url>`. For SSH, verify the server host key and configure your SSH agent; the app uses strict known-host verification and noninteractive authentication. For HTTPS, use your trusted named Git credential helper. Never put passwords in clone URLs or settings.
2. Start the app and choose **Open Git branch...**. Enter a clone URL and short branch name, such as `feature/my-task`.
3. If the repository is unknown, a registration window appears. Confirm its URL, local source clone, workspace root and baseline regexes. Choose an existing clone, or allow creation of a **new bare clone** at an absent path. Registration requires your explicit confirmation; a browser link never grants repository trust automatically.
4. The app fetches the branch, pins its commit, creates a detached worktree and opens VS Code. Reopening the same branch preserves local commits and staged, unstaged, untracked and ignored files. It does not reset or silently update an existing owner.

**Why a source clone?** Git worktrees share an object database and worktree registry. The source is that local object store, either an existing normal checkout or a bare clone with no working files. The app can create a bare clone for you; you do not need a separate manually maintained checkout. Keep this source clone in backups along with worktrees, parked data, snapshots and the closed journal.

Supported URL examples:

```text
https://github.com/HRAshton/git-workspace-pool.git
ssh://git@bitbucket.company.example:7999/TEAM/repository.git
git@bitbucket.company.example:TEAM/repository.git
```

The first is a real existing repository, usable with branch `main`. The company SSH examples are placeholders. Transport, SSH username, repository path case and explicit ports matter; SSH and HTTPS spellings of a repository are separate registrations. scp-style paths are relative to the remote account, so they are not silently equated to absolute `ssh://` paths.

## Browser link contract

Any browser button can open this provider-independent link:

```javascript
const link = 'prworkspace://open/?v=2&repo=' + encodeURIComponent(cloneUrl) +
    '&branch=' + encodeURIComponent(shortBranchName);
window.location.assign(link);
```

Example for the existing project repository:

```text
prworkspace://open/?v=2&repo=https%3A%2F%2Fgithub.com%2FHRAshton%2Fgit-workspace-pool.git&branch=main
```

The bundled `git-branch-workspace.user.js` is a minimal Tampermonkey button. Replace its placeholder `@match` with your exact site. No GitHub/Bitbucket page API is used. The old GitHub PR userscript, PR settings tab and v1 PR-link entrypoint are retired. Existing journal/settings evidence is retained; legacy records are not automatically adopted as new branch owners. Provider parsers, origin adapters and PR fetch code exist only in a test-support project outside the shipped app dependency graph.

## Baseline regexes

New registration defaults to `^master$`. Use one regex per line, for example:

```text
^master$
^20\d\d[rR]\d{3}$
```

The production pattern matches names such as `2026r150` and `2026R151`. Matching is against advertised remote short branch names, never a fetch refspec or a local checkout. Empty patterns disable background refresh. Existing exact-name settings remain readable and are converted to anchored literal regexes when saved in Settings.

Regexes use a bounded linear-time engine without lookarounds/backreferences. Discovery and fetch have deadlines and interval backoff; inventories and matches are bounded. Fetch updates only dedicated app refs, with no checkout, pruning, builds or dependency installation. See [baseline and reuse details](docs/VERIFIED_REUSE.md).

## Tray and editor

- **Open Git branch... / Register repository...** for opening and trust configuration. Double-clicking the tray opens the branch dialog.
- **Workspaces** for suspend, park/unpark and retained workspaces.
- **Reuse and baselines** for explicit released-slot selection, compatible npm copying and baseline status/refresh.
- **Snapshots and recovery** for capture, isolated restore, interrupted checkout and release recovery.
- **Diagnostics** for local storage and opt-in timings; **Settings** for repositories, limits and editor.

The editor setting defaults to `code.exe`. The app resolves it on PATH, including VS Code's usual `bin` entry, and launches the actual executable directly. An absolute executable path is also supported. Shell aliases, commands and `code.cmd` are not executed.

## Attested candidate

A trusted-main [Attested Windows candidate workflow](.github/workflows/attest.yml) runs normal/extended tests and package/backup checks, builds the self-contained ZIP, creates SLSA provenance in an in-toto attestation with Sigstore identity, then verifies repository, workflow, main ref and exact source commit. It archives the ZIP, checksum, manifest and attestation bundle. PR preview ZIPs are development artifacts, not trusted-main attested candidates.

No Authenticode certificate or MSI signing is required for this RC. Executables remain unsigned, so Windows may show publisher/SmartScreen warnings. A checksum or package manifest alone is not an attestation. Follow [verification instructions](docs/ATTESTATION.md) before running the installer. No SLSA level or overall OpenSSF certification is claimed.

Extract a verified ZIP and run `.\Install-PerUser.ps1`. It stages a versioned per-user install, verifies its complete file inventory and startup probe, preserves old versions/data, then registers `prworkspace:` for the current user. No .NET SDK is needed to run the packaged app. Stop the old tray and back up closed state/worktrees before upgrading. `Unregister-PerUser.ps1` removes only the matching protocol registration and never deletes data.

## RC completion

Use [the current roadmap](ROADMAP.md) and [Windows/Bitbucket acceptance](docs/BITBUCKET_ACCEPTANCE.md). Live company SSH/HTTPS authentication, browser/tray/editor integration, real upgrade/rollback and representative failure/performance checks require the target machine. Automatic cleanup/eviction, extra artifact classes and ReFS acceleration are outside this RC scope.

For development on Windows with the pinned .NET SDK and PowerShell 7:

```powershell
.\scripts\Test-Changes.ps1 -IncludeSlow
.\scripts\Test-Changes.ps1 -IncludeSlow -IncludePackage -OutputDirectory C:\gwp-artifacts\new-validation
```

Build outputs must be new and outside a clean committed checkout. See [recovery](docs/RECOVERY.md), [operations](docs/OPERATIONS.md) and [journal upgrades](docs/LIFECYCLE_UPGRADE.md). Configuration is under `%APPDATA%\GitWorkspacePool`; journal/snapshot state is under `%LOCALAPPDATA%\GitWorkspacePool`.
