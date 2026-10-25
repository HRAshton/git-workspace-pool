> Historical documentation for [the archived implementation](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite/docs). It does not describe the new warm-worktree engine.

# Git Workspace Pool 0.1.0-rc.1 — engineering release candidate

**Platform:** Windows 11 x64. **Distribution:** unsigned, self-contained ZIP. **Status:** available for acceptance testing; not approved as a signed public release.

## RC1-supported workflow

- Install into the current user's profile without administrator rights; smoke-check staged executable before publishing the installed version and registering the current-user protocol.
- Install the included Tampermonkey userscript, configure a trusted local GitHub.com clone and workspace root, then use **Open locally** on a GitHub pull request to open its commit-pinned Git worktree in VS Code.
- Reopen the same PR without resetting staged, unstaged, untracked or ignored data. SQLite journal keeps exclusive session ownership.
- Use the explicitly confirmed suspend, park/unpark, snapshot, side-by-side restore and read-only recovery diagnostics operations. Advanced manual slot-release and artifact-copy commands are experimental, not yet certified for general RC1 use.
- Automatic worktree recycling, eviction, deletion, cleanup, speculative PR prediction, background baseline fetching and building/restoring dependencies from user projects remain disabled or unimplemented.

## Build and verification

The RC1 Windows workflow produces `GitWorkspacePool-win-x64-0.1.0-rc.1.zip` with its adjacent `.sha256`, `RC1_BUILD_EVIDENCE.txt` recording exact source commit and hash, and this release note. It uses .NET 10, `win-x64`, self-contained single-file publish, version `0.1.0-rc.1`, and dedicated no-UI installer smoke. The workflow runs extended real-process snapshot/recovery crash E2E. These checks are **not code-signing, attestations, GUI acceptance or upgrade-from-an-older-version proof**.

## Limits and excluded release features

- GitHub.com PRs with matching configured `origin` and local VS Code are supported; enterprise provider remote matching, GitLab, Bitbucket, Azure DevOps and fork-specific PR fetch strategies are not certified.
- The ZIP is unsigned; no MSI/MSIX, signing certificate, auto-updater or published GitHub Release is supplied by this candidate workflow.
- Headless CI does not prove the installed tray, real browser/Tampermonkey, protocol prompt, VS Code editor, Windows Defender, anti-virus locks or all filesystem race cases.
- Database changes may require forward SQLite migrations; do not open a newer migrated journal with an older binary. Back up the journal, settings, snapshots and worktrees before installing.
- Open filesystem alias/reparse, deletion-replacement, named credential helper and real disk-full edge cases prohibit unattended release or cleanup.

## Release approval

Complete every required item in [RC1 acceptance](RC1_ACCEPTANCE.md), including manual Windows, security, older-install migration, rollback, benchmark and signing gates, before calling this a release-approved RC1. The full [roadmap](../ROADMAP.md) includes broader features explicitly deferred from this candidate.
