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

No admin rights or background Windows service are required. The browser protocol is untrusted input: only recognized GitHub PR URLs matching configured repositories are accepted.

## Testing

The fast E2E suite uses a temporary local Git remote with GitHub-style PR refs, real Git worktrees, the versioned URL protocol, and real Windows Named Pipes. It uses a fake editor and **does not launch VS Code, the tray UI, or a browser**.

```powershell
dotnet test tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj --filter "Category!=FastE2E"
dotnet test tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj --filter "Category=FastE2E"
```

These tests run on every PR in Windows CI. [Roadmap and acceptance gates](ROADMAP.md) distinguish this fast lane from the later, slower installed-browser-and-editor smoke test.

## Non-goals

The app never runs build, npm install, dotnet restore or untrusted PR scripts. Worktree recycling, snapshots, artifact policies, GitLab/Bitbucket adapters and intelligent selection are planned later; **do not use this preview to auto-clean or reassign worktrees**.

## Engineering principles

C# style follows [WinCfApiWrapper](https://github.com/HRAshton/WinCfApiWrapper): CSharpier formatting, StyleCop analyzers, file-scoped namespaces, explicit types unless evident, and `_field` naming. See [architecture](docs/ARCHITECTURE.md).
