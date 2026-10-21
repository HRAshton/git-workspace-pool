# Git Workspace Pool

A Windows tray application that opens GitHub pull requests in local VS Code worktrees. A Tampermonkey userscript supplies the browser action.

## Status

Early MVP. The initial vertical slice uses explicitly configured local repositories and creates or reopens a dedicated worktree per PR. It does **not** compile projects, install packages, recycle occupied worktrees, or delete user files.

## Getting started (Windows, .NET 10 SDK)

1. Build the solution: `dotnet build GitWorkspacePool.slnx`.
2. Start `GitWorkspacePool.Tray` once to create `%APPDATA%\GitWorkspacePool\settings.json` and register the per-user `prworkspace:` handler.
3. Configure a local repository and workspace root in `settings.json` using the documented example there.
4. Run the tray application and install `userscript/git-workspace-pool.user.js` in Tampermonkey.
5. Open a GitHub PR and click **Open locally**. The first request fetches that PR and creates a detached worktree. Subsequent requests reopen it without checkout, protecting local edits.

No admin rights or background Windows service are required. The browser protocol is untrusted input: only recognized GitHub PR URLs matching configured repositories are accepted.

## Non-goals

The app never runs build, npm install, dotnet restore or untrusted PR scripts. Worktree recycling, snapshots, artifact policies, GitLab/Bitbucket adapters and intelligent selection are planned later; **do not use this preview to auto-clean or reassign worktrees**.

## Engineering principles

C# style follows [WinCfApiWrapper](https://github.com/HRAshton/WinCfApiWrapper): CSharpier formatting, StyleCop analyzers, file-scoped namespaces, explicit types unless evident, and `_field` naming. See [architecture](docs/ARCHITECTURE.md).
