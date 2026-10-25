# Git-only RC roadmap

This RC opens an HTTPS/SSH Git repository and branch in an owned VS Code worktree. No provider APIs, provider PR URLs or Authenticode signing are required. Historical aspirational work is archived in [the earlier roadmap](docs/HISTORICAL_ROADMAP.md); it is not a claim that those features were implemented.

## Implementation in this step

- [x] Provider-independent repo/branch flow, exact commit pinning and edit-preserving reopen.
- [x] HTTPS and SSH/scp-style URL validation, origin matching, safe noninteractive authentication and strict SSH known-host checking.
- [x] Explicit unknown-repository registration, existing normal/bare source selection or creation of a new bare source clone.
- [x] Baseline regexes over bounded advertised remote heads, default `^master$`, including `^20\d\d[rR]\d{3}$`, interval/backoff and no checkout.
- [x] Regrouped tray menu, embedded multi-size app icon and default `code.exe` resolution.
- [x] Shared snapshot/restore, suspend, park/unpark, explicit released-slot handoff and copy-only compatible npm reuse.
- [x] Settings, cancellation, read-only storage warnings, opt-in timings and retained recovery evidence.
- [x] Trusted-main attestation workflow and verification policy implemented. Actual attestation execution remains a gate below.

## Evidence needed to complete this RC step

- [ ] Current-head normal Windows CI and extended crash/package/backup validation pass, with no unresolved blocking findings.
- [ ] Trusted-main candidate build succeeds and its in-toto/SLSA attestation verifies for the exact source commit and ZIP digest. Archive ZIP, checksum, manifest, bundle and run/test evidence.
- [ ] On your Windows machine, prove actual company Git authentication and host trust, registration/create-bare and existing-clone flows, browser protocol consent and VS Code startup.
- [ ] Prove staged/unstaged/untracked/ignored data and local commits survive reopen/restart, snapshot/restore and explicit lifecycle operations in a disposable repo.
- [ ] Exercise a backed-up actual prior profile upgrade and matching-state rollback; confirm interrupted/error/locked-file/low-space behavior retains originals and evidence.
- [ ] Record representative latency/storage results and real AV/filesystem behavior; accept remaining limitations and approve this exact candidate.

No signing certificate is needed. These checks define RC acceptance, not a universal security certification. Automated output must not be substituted for live authentication or GUI acceptance. See [acceptance procedure](docs/BITBUCKET_ACCEPTANCE.md) and [attestation verification](docs/ATTESTATION.md).

## Outside this RC

Automatic deletion/eviction/recycling, generic build-artifact policies (`bin`/`obj`), predictive warming, project execution, ReFS acceleration and provider PR integrations. The app never installs dependencies, runs a project build/test, deletes original work or silently adopts unknown worktrees. Retention remains explicit and conservative; disk warnings do not authorize cleanup.
