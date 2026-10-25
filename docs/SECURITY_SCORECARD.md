# OpenSSF Scorecard and prerelease security checklist

The live [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/HRAshton/git-workspace-pool) measures repository security practices; it is not a product safety certificate, a REUSE license audit, or evidence of installed Windows acceptance. The badge is dynamic and no numerical score is promised in this document.

## Already configured in source

- [x] 0BSD repository license and REUSE 3.3 file-level metadata with a REUSE CI lint gate.
- [x] `SECURITY.md` reporting guidance and `CONTRIBUTING.md`.
- [x] Pin external GitHub Actions to full immutable commit SHAs.
- [x] Give CI read-only permissions by default; isolate elevated permissions to attestation and Scorecard publication jobs.
- [x] Dependabot weekly version updates for NuGet, Actions and the pinned .NET SDK.
- [x] Signed GitHub artifact *attestation/provenance* workflow for manually triggered candidate builds. This is not code-signing of the Windows executable.
- [x] Scorecard job on `main` push and weekly, using GitHub OIDC to publish results and upload SARIF.
- [x] CodeQL C# SAST analysis on PRs, main and weekly, with a manual .NET build on Windows.

## Maintainer settings to verify before release

These are **not enabled merely by committing files**. Confirm each in GitHub repository settings; do not check a box without verifying it.

- [ ] Protect `main` using GitHub rulesets: require PR reviews by an independent contributor where feasible, required Windows CI + REUSE checks, forbid force pushes/deletions and review administrator bypasses. The connected GitHub App does not have administration access to configure these settings.
- [ ] Enable Dependabot alerts/security updates, secret scanning and push protection where available. Check the Dependency graph for recognized NuGet manifests.
- [ ] Enable private vulnerability reporting or establish a secure contact channel for `SECURITY.md`.
- [ ] Review the first [measured Scorecard run](https://github.com/HRAshton/git-workspace-pool/actions/runs/40210850213): **5.9/10** on October 25, 2026, before the CodeQL workflow was added. Confirm new CodeQL findings are reviewed and fixed; SAST scoring considers recent commits and may take time to improve. Other 0/10 checks include Branch-Protection and Code-Review, which require real governance controls.
- [ ] Check authorship/licensing of repository image assets and any third-party snippets before claiming that the blanket first-party 0BSD declaration covers them.
- [ ] Record installed Windows browser → repo/branch → editor acceptance and real monorepo warm/reopen vs fresh median/p95 as tracked in [issue #39](https://github.com/HRAshton/git-workspace-pool/issues/39).
- [ ] For a public candidate, run the manually gated Windows attestation workflow from trusted `main` and verify source/digest/provenance before publishing.

The baseline score is measured, not a target or certification; it will evolve with CodeQL coverage, protected-branch reviews, project maturity, contributors, releases and vulnerability monitoring. Prefer genuine controls over badge-driven changes.
