# Generic Git branch workspaces (including Bitbucket Data Center 9)

Open a configured **HTTPS Git repository + branch** in a detached VS Code worktree. No pull request, Bitbucket REST API, API token, or Bitbucket-specific fetch ref is needed. The source repository may be on Bitbucket Data Center 9 or another HTTPS Git server.

## Configure once

Clone the repository normally with Git for Windows. Authenticate using your company's supported username/password flow and Git Credential Manager. For example, run `git ls-remote origin` from that clone in your own terminal and complete authentication before using the tray. Never put a password in a clone URL, protocol link or settings file. The app does not implement a second password store or API login.

Use **Settings... → Git repositories** to enter the clone URL, local clone and separate workspace root. **Check selected origin** verifies identity without fetching. Alternatively, add `GitRepositories` to `%APPDATA%\GitWorkspacePool\settings.json` alongside any existing settings, then restart the tray:

```json
{
  "GitRepositories": [
    {
      "CloneUrl": "https://bitbucket.company.example/scm/team/my-repo.git",
      "LocalPath": "C:\\src\\my-repo",
      "WorkspaceRoot": "C:\\src\\branch-workspaces"
    }
  ]
}
```

Use the **exact HTTPS clone path**, including a context path or custom port if needed. Host case and a trailing slash are normalized; repository path case is preserved. The clone's actual `origin` must match. A username-only `origin` such as `https://alice@host/scm/team/repo.git` is accepted; the configured `CloneUrl` remains credential-free. SSH and plain HTTP are outside this generic path's supported scope. Existing GitHub PR settings and their v1 protocol remain supported separately.

`WorkspaceRoot` must be separate from the source clone, and source/root ancestry must not use symlinks or junctions. Register a fork as its own clone if you want its source branch. This feature never assumes a fork branch exists in the destination repository.

## Open without a browser

Choose **Open Git branch...** in the tray, select the configured repository and enter a **short branch name**, for example `feature/my-task` (not `refs/heads/feature/my-task`). The app fetches that branch from the clone's `origin`, records its exact commit in SQLite, creates a detached worktree and opens VS Code.

Git handles credentials using its configured, trusted named helper such as `manager`. Existing restrictions on shell/path credential helpers, URL rewriting, custom SSH commands, executable filters and hooks still apply. HTTPS redirects are disabled for branch fetches; configure the final clone URL. If your server allows only browser SSO and does not permit Git username/password authentication, use the credential mechanism approved by your server administrator. TLS verification stays enabled.

## Tampermonkey / your own button

The entire browser contract is:

```javascript
const link = 'prworkspace://open/?v=2&repo=' + encodeURIComponent(gitCloneUrl) +
    '&branch=' + encodeURIComponent(shortBranchName);
window.location.assign(link);
```

Example decoded values: `repo=https://bitbucket.company.example/scm/team/my-repo.git`, `branch=feature/my-task`. Encode each field exactly once. Do not send credentials, a PR URL, command-line options or arbitrary refspecs. Duplicate or unknown fields are rejected, and unconfigured repositories cannot be opened.

The ZIP includes `git-branch-workspace.user.js` as a minimal prompt-based helper. Replace its `@match` placeholder with your exact company host. You can replace its prompts with your own DOM-based clone/branch picker without changing the backend; no API integration is required. The helper intentionally does not guess the source branch from unstable Bitbucket page markup.

## Reopen and recovery

- A repository/branch pair owns one stable workspace. Branch names are case-sensitive; unrelated servers and branches have separate identities and paths.
- Reopening preserves staged, unstaged, ignored and untracked files, plus your own local commits. It does **not** refetch, reset, switch branches or silently update to a newer remote head.
- Checkouts are detached. To publish commits, use an explicit Git push destination or create a local branch in VS Code. No background push or merge occurs.
- If interrupted before checkout, reopening retries at the already journalled commit even if the remote branch has moved. If a checkout already exists, recovery requires its registered, clean, pinned identity.
- Dirty, missing or mismatched interrupted worktrees are retained for inspection and marked `RecoveryRequired`. **Git branch recovery (read-only)...** lists the first 100 unfinished operations, their paths and pinned commits. There is no automatic repair, adoption, cleanup or deletion.
- Branch ownership now uses the shared session/slot journal (schema v9), so the workspace snapshot, restore, suspend, park/unpark, release, retained-open and compatible dependency-transfer menus also support branches. Legacy v8 branch records are migrated without moving files or changing operation IDs. Back up the closed journal/settings and worktrees before upgrading; older binaries refuse the newer schema. See [upgrade details](LIFECYCLE_UPGRADE.md).
- To reuse a slot, park a branch workspace, explicitly release its slot while retaining the parked files, then choose **Open workspace in a released slot...** and enter the next branch. Reopening the released branch opens its retained parked worktree. Compatible ignored npm dependencies can be copied with the existing explicit transfer menu; incompatible or linked files are refused.
- Keep the source clone and internal `refs/git-workspace-pool/branches/*` refs. They retain pinned commit objects needed by unfinished operations. No pruning policy is implemented.

## Acceptance on your machine

Use a disposable configured repository first. Open a branch from the tray, then from your browser button. Check VS Code shows the expected detached commit. Add different staged/unstaged content, an untracked file and an ignored file; reopen and restart the tray and verify they remain intact. Confirm a different branch opens elsewhere. Verify actual company authentication, protocol prompts, VS Code and antivirus behavior on Windows; headless CI does not certify those.

Automated coverage uses a local bare Git remote and real worktrees/SQLite/locks, replacing only the provider origin identity in the offline fixture. It covers protocol encoding, untrusted URLs/refspecs, creation, reopen, concurrent opens, failed fetch/retry, pinned restart recovery, dirty interruption refusal and wrong-origin rejection. It does not contact a live Bitbucket DC server.
