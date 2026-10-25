> Historical documentation for [the archived implementation](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite/docs). It does not describe the new warm-worktree engine.

# Verified reuse choices and configured baselines

## Reuse choices

**Open branch with reuse choices...** accepts the same configured HTTPS clone and short branch as normal opening. Existing branch owners always reopen first, without a fetch or pool allocation. For a new owner, the application fetches an immutable target into a stable preview ref, then inspects a bounded list of explicitly released slots.

A candidate must have a Ready slot at an empty, unregistered path, the same configured source clone and workspace root, and a retained Released donor with the exact expected parked path, Git registration and saved head. Unknown, occupied, linked or unverifiable candidates are excluded, not repaired. Candidate collection does not reserve, park, release, clean or move anything.

Choices are ordered using the existing deterministic ranking: matching captured head, then observed changed-path count and stable slot identity. The tracked path count comes from a bounded Git commit-to-commit comparison with external diff/textconv disabled. All target slots are empty and all original donor artifacts remain retained, so selection invalidates zero user artifact bytes. No guessed timing multiplier or physical disk-saving estimate is used. A selected slot still requires a fresh checkout even when the donor head matches.

Confirming a candidate binds the new checkout to the **captured preview commit**, even if the remote branch advances afterward. Allocation independently rechecks generation, ownership, paths and Git registration. Stale selections fail closed. You may instead choose **Open fresh normally**, which fetches the then-current branch through the ordinary open path.

Optional npm-copy eligibility compares the donor's manifests against the captured target, checks ignored/untracked state and inventories bounded regular files without following links. The checkbox is off by default. Actual copying performs the existing strict post-checkout compatibility and per-file integrity checks again; preview eligibility is advisory. If copying fails or is cancelled, original donor files stay intact and an incomplete destination remains for inspection. Do not assume a partial node_modules is usable. No dependency installation, project test or build runs.

Settings can optionally offer this chooser for browser/new branch opens. This enables an explicit choice, not unattended worktree recycling. Performance acceptance is still needed before claiming an actual workflow speedup.

## Baseline regexes

New registration uses `^master$`. Configure one regex per line (Shift+Enter in the Settings grid); for example `^20\d\d[rR]\d{3}$` matches `2026r150` and `2026R151`. Empty regexes disable refresh. The regexes select only safe advertised remote short heads; they are never passed as refspecs. Old exact-name lists remain readable and become escaped anchored literal patterns when saved in Settings, retaining an old empty list as disabled.

The engine is linear-time with bounded pattern length/count, without lookarounds/backreferences. At most 10,000 advertised heads and 256 matches per repository are allowed; excessive/invalid input fails closed. Discovery is cached for the configured interval (30 minutes by default, 15–1440 supported), failed discovery backs off, and removed configurations are pruned from status. A pass discovers at most four due repositories and fetches at most four due refs sequentially; each network operation has a two-minute deadline. More work is handled in later passes, and quitting cancels it.

Fetched commits live only in dedicated `refs/git-workspace-pool/baselines/*` refs. Checked-out files, source HEAD, unrelated tracking refs and user work are unchanged. No pruning, project execution, dependency installation or predictions run. **Reuse and baselines** contains manual refresh and status, including discovery failures. Authentication stays in Git, using SSH agent/known-hosts or an HTTPS credential helper.

## Evidence-storage warnings

Settings exposes optional snapshot, recovery and combined **logical byte warning thresholds** (0 disables a threshold in the UI). These use only a successfully completed, bounded read-only inventory of app-owned snapshots/recovery stores. Equality does not warn; negative/overflowing usage and invalid settings fail closed. Unknown usage is not zero.

Warnings are advisory. They are not a filesystem quota, physical NTFS allocated-space measurement, worktree eviction policy or permission to delete files. Active, parked, recovery-needed and snapshot data are never deleted because a threshold was exceeded. The existing snapshot export/transfer hard resource ceilings still apply.

## Git command and snapshot retention hardening

Application Git commands clear inherited repository/index/object-store/config-injection and trace overrides, disable replacement objects, and keep HTTPS certificate verification enabled. Fetches use only their explicit app-owned destination ref, with refmap/pruning, submodule recursion and automatic maintenance disabled. Unrelated tracking refs and source edits are left unchanged. Ordinary SSH agent/credential-helper configuration remains supported where that provider path supports it.

Git output is bounded (16 Mi characters stdout, 1 Mi stderr); unsupported oversized or invalid UTF-8 inspection metadata fails closed. Snapshot status still disables optional index refresh and now rejects newly configured executable filters before inspecting working files. Configuration and filesystem replacement races remain separate hardening gates; this is not permission to enable automatic deletion.

A published snapshot now pins its captured commit under `refs/git-workspace-pool/snapshots/<id>` before publishing its payload. This protects a later user commit even after amend/reset, reflog expiry and ordinary Git pruning. Failed or unjournalled publication retains its pending evidence/ref. Keep the source clone's Git objects and internal refs in backups, in addition to worktrees and snapshot payloads. No automatic snapshot-ref pruning is implemented.
