# Git Workspace Pool

A Windows tray application that opens GitHub pull requests in local VS Code worktrees. A Tampermonkey userscript supplies the browser action.

## Status

Early MVP. The initial vertical slice uses explicitly configured local repositories and creates or reopens a dedicated worktree per PR. New workspaces are reserved in a local SQLite journal before checkout and Git commit identity is pinned for safe recovery. It does **not** compile projects, install packages, recycle occupied worktrees, or delete user files.

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

These tests run on every PR in Windows CI. [Roadmap and acceptance gates](ROADMAP.md) distinguish this fast lane from the later, slower installed-browser-and-editor smoke test.

## Explicit session suspension

The tray menu provides **Suspend active PR (keep worktree)...**. This only changes the durable activity state: the original Git worktree, all local modifications and exclusive PR ownership stay untouched. Opening the same PR resumes it. **Suspension does not free disk space, release a warm slot or enable recycling.** A worktree in `RecoveryRequired` can no longer be reactivated by normal PR opening.

## Experimental workspace snapshots

The tray menu provides **Snapshot active PR...** for an explicit, non-destructive snapshot. Captures include the Git index, staged blobs independently from unstaged working files, tracked changes, untracked and ignored files (including binary data), deletions and a SHA-256 manifest. Snapshots are stored under `%LOCALAPPDATA%\\GitWorkspacePool\\snapshots`. Only explicitly selected active sessions are eligible.

A snapshot has a conservative **50,000-file / 2 GiB** budget and refuses symlinks/junctions. It does not clean, release, rename, or recycle a worktree. Incomplete snapshots remain marked `.pending-*` for inspection; a published snapshot remains intact if journal registration fails. Use **Restore snapshot to new worktree...** to reconstruct an isolated worktree under `%LOCALAPPDATA%\\GitWorkspacePool\\recovered`. The restore checks the manifest, file hashes and original Git index, then verifies that the recovered Git status matches the saved status. It never writes to or switches the original PR session.

**Limitations:** recovery worktrees are deliberately outside the managed pool and are not automatically adopted or cleaned. Interrupted restores may leave a separate worktree for inspection. Snapshots still do not authorize automatic release, move, cleanup or recycling until the remaining fault-injection and recovery cases are implemented.

Use **Recovery diagnostics...** to inspect interrupted snapshots, missing journalled data, and isolated recovery worktrees. This is **read-only** and does not reclaim storage. See the [safe recovery runbook](docs/RECOVERY.md).

## Non-goals

The app never runs build, npm install, dotnet restore or untrusted PR scripts. Worktree recycling, automatic snapshot adoption, artifact policies, GitLab/Bitbucket adapters and intelligent selection are planned later; **do not use this preview to auto-clean or reassign worktrees**.

## Engineering principles

C# style follows [WinCfApiWrapper](https://github.com/HRAshton/WinCfApiWrapper): CSharpier formatting, StyleCop analyzers, file-scoped namespaces, explicit types unless evident, and `_field` naming. See [architecture](docs/ARCHITECTURE.md).
