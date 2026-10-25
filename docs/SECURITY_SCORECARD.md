# OpenSSF Scorecard and prerelease security checklist

The live [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/HRAshton/git-workspace-pool) measures repository security practices; it is not a product safety certificate, a REUSE license audit, or evidence of installed Windows acceptance. The badge is dynamic and no numerical score is promised in this document.

## Already configured in source

- [x] 0BSD repository license and REUSE 3.3 file-level metadata with a REUSE CI lint gate.
- [x] `SECURITY.md` reporting guidance and `CONTRIBUTING.md`.
- [x] Pin external GitHub Actions to full immutable commit SHAs.
- [x] Give CI read-only permissions by default; isolate elevated permissions to attestation and Scorecard publication jobs.
- [x] Renovate is configured to propose one grouped non-major dependency PR each Monday (even when only one update exists) and automatically merge it after checks pass. Major upgrades remain manual. Renovate must also be installed on the repository for updates to run.
- [x] Signed GitHub artifact *attestation/provenance* workflow for manually triggered candidate builds. This is not code-signing of the Windows executable.
- [x] Scorecard job on `main` push and weekly, using GitHub OIDC to publish results and upload SARIF.
- [x] CodeQL default setup is enabled in GitHub settings by the maintainer; the redundant custom CodeQL workflow has been removed. GitHub's default setup owns scanning frequency and languages.

## Maintainer settings to verify before release

These are **not enabled merely by committing files**. Confirm each in GitHub repository settings; do not check a box without verifying it.

- [ ] Protect `main` using GitHub rulesets: require passing Windows CI and REUSE checks, independent approval for ordinary contributions where feasible, forbid force pushes/deletions and review administrator bypasses. Permit the Renovate bot to merge passing update PRs only if compatible with these rules. The connected GitHub App does not have administration access to configure these settings.
- [ ] Install/authorize the [Renovate GitHub App](https://github.com/apps/renovate) for this repository and verify it has the permissions needed to open/merge PRs. The old Dependabot *version-update* config is removed, but keep Dependabot security alerts/updates, secret scanning and push protection enabled where available. Check the Dependency graph for recognized NuGet manifests.
- [ ] Enable private vulnerability reporting or establish a secure contact channel for `SECURITY.md`.
- [ ] Monitor [the latest measured Scorecard](https://github.com/HRAshton/git-workspace-pool/actions/runs/40211443714): **6.3/10** after introducing CodeQL, before removing the custom workflow in favor of GitHub-managed default setup. Verify default scanning coverage and review findings; SAST scoring measures recent commits and may change. Branch-Protection and Code-Review remain 0/10 until real governance controls exist.
- [ ] Check authorship/licensing of repository image assets and any third-party snippets before claiming that the blanket first-party 0BSD declaration covers them.
- [ ] Record installed Windows browser → repo/branch → editor acceptance and real monorepo warm/reopen vs fresh median/p95 as tracked in [issue #39](https://github.com/HRAshton/git-workspace-pool/issues/39).
- [ ] For a public candidate, run the manually gated Windows attestation workflow from trusted `main` and verify source/digest/provenance before publishing.

The baseline score is measured, not a target or certification; it will evolve with CodeQL coverage, protected-branch reviews, project maturity, contributors, releases and vulnerability monitoring. Prefer genuine controls over badge-driven changes.

## Renovate behavior

- Dependency updates are evaluated on Mondays in the `Europe/Belgrade` timezone. Renovate's service runs on its own cadence, so this limits the allowed day rather than guaranteeing a particular hour.
- `minor`, `patch`, `pin` and `digest` updates share one named group, including a singleton: `minimumGroupSize: 1` and `groupSingleUpdates: true`. No wait for three updates. Major updates stay separate for manual review.
- Only non-major groups request PR automerge after status checks pass. `platformAutomerge: false` uses Renovate's own merge logic instead of GitHub's currently disabled *Allow auto-merge* switch. The Renovate app must be installed and allowed to merge; branch rules/status checks still apply.
- Do **not** remove GitHub Dependabot *security alerts*. Only Dependabot's overlapping scheduled version-update PR configuration was removed.
