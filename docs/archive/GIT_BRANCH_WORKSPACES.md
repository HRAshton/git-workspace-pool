> Historical documentation for [the archived implementation](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite/docs). It does not describe the new warm-worktree engine.

# Git repository + branch workspaces

Use a Git clone URL (HTTPS, `ssh://` or `user@host:path`) and a short branch name. No provider API or PR is required. See [setup, real repository example and browser link contract](../README.md).

Unknown repositories open a registration window with the requested URL, source clone location, workspace root, clone mode and baseline regexes. Explicit confirmation is required. You may use an existing normal/bare clone, or create a new bare object store at an absent path. The app verifies its actual origin before saving. Failed/cancelled clones are retained; retry does not adopt or overwrite them. Inspect the partial folder, or choose a new destination.

SSH uses an explicit OpenSSH client command with batch mode and strict known-host checking, plus your SSH configuration/agent. Verify and trust the host key in your own terminal before using the app. No password prompt, private key storage, custom SSH command or host-key bypass is added. HTTPS uses a trusted named Git credential helper; configure the company CA instead of disabling certificate verification. URLs must not contain passwords. URI/scp username, path case and transport identity are preserved; scp-relative paths are not equated to absolute SSH URI paths.

The source clone owns shared Git objects and worktree metadata. Keep it and internal `refs/git-workspace-pool/*` refs, including snapshot retention refs. A bare source requires no working files. Back it up with worktrees, parked folders, snapshots and closed profile state.

A branch owns one stable workspace. Reopen preserves local commits and all file states without refetch/reset; new owners pin an immutable fetched commit. Checkouts are detached: create a local branch or explicitly select a push destination to publish your commits. The app never pushes automatically.

Snapshots/isolated restore, suspend, park/unpark, explicit release/retained reopening, verified released-slot choice and compatible npm copy all use the same ownership journal. Unknown/dirty interrupted worktrees are retained for recovery, never adopted or cleaned automatically. See [upgrade](LIFECYCLE_UPGRADE.md), [operations](OPERATIONS.md) and [recovery](RECOVERY.md).

The tray accepts only v2 repo/branch links. Retired provider-specific PR links/settings/menus are not active. Their adapters/fetch code are isolated in test-only support and are not compiled into the shipped libraries. Earlier journal rows and opaque old settings remain retained as evidence; removing a provider menu never migrates ownership or deletes user data.

See [baseline regex and reuse behavior](VERIFIED_REUSE.md), [target-machine acceptance](BITBUCKET_ACCEPTANCE.md) and [attestation verification](ATTESTATION.md).
