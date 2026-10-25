# Git-only engineering candidate

Windows x64 self-contained ZIP. Uses ordinary Git repository + branch inputs over HTTPS or SSH, independent of hosting provider. This step adds explicit unknown-repository registration and managed bare source creation, regex baselines (default `^master$`), regrouped tray controls, an embedded icon and `code.exe` as the editor default.

Provider-specific PR links, settings and browser helper are retired. Original journal/settings evidence is preserved; old PR owners are not automatically turned into branch owners. Existing exact baseline settings remain readable and become anchored literal regexes on Settings save. Existing source clone/workspace paths are not moved.

The trusted-main workflow produces and verifies an in-toto/SLSA build attestation for the candidate ZIP after normal/extended tests, package and backup checks. A PR preview is not that attested candidate. See `ATTESTATION.md`. Executables remain unsigned; no Authenticode or MSI certificate is required by this RC policy.

Snapshot/restore, suspend, parking, explicit release/retained reopening and compatible copy-only npm reuse preserve ownership and user evidence. No automatic disposal, cleanup, build, dependency install or push is enabled. Back up the source clone/object store, worktrees, parked folders, snapshots and closed database/settings before upgrade; the included backup helper alone covers only profile state.

The remaining approval gates are the current [roadmap](../ROADMAP.md) and bundled `BITBUCKET_ACCEPTANCE.md`: live company credentials/SSH trust, installed browser/tray/editor, real prior-profile upgrade/rollback and actual failure/performance behavior. A passing CI build or attestation is not proof those passed.
