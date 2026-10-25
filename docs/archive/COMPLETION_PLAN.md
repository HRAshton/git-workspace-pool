> Historical documentation for [the archived implementation](https://github.com/HRAshton/git-workspace-pool/tree/archive/pre-in-place-rewrite/docs). It does not describe the new warm-worktree engine.

# Current completion contract

The active scope is the [Git-only RC roadmap](../ROADMAP.md): HTTPS/SSH repository + branch, explicit registration, optional managed bare source, regex baselines, VS Code and existing non-destructive lifecycle features. GitHub PR integration and certificate signing are no longer product/release requirements.

Implementation and acceptance are separate. The roadmap's evidence checklist is the authoritative remaining work. Current-head Windows checks, a successful verified trusted-main in-toto/SLSA attestation, and actual company/browser/editor/upgrade acceptance must be recorded before RC approval. A green PR or a workflow definition alone does not complete acceptance.

The source clone is the Git object database and worktree registry; it may be normal or bare. Back it up alongside worktrees, parked trees, snapshots and closed database/settings. Older provider-owned journal records remain evidence and are not reassigned automatically; retired provider controls are not exposed by the tray. Legacy exact baseline names remain readable; saving Settings converts them to anchored literal regexes. New registration starts with `^master$`, and empty regexes disable refresh.

Automatic deletion/eviction, arbitrary artifact classes and ReFS acceleration remain extensions outside this RC. No cleanup capability is enabled to make the checklist look complete.
