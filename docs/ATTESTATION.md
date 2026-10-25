# Attested Windows test previews

The repository builds self-contained **Windows x64** candidates with GitHub Actions. The GitHub Release is always marked **prerelease**; the executable is **not Authenticode code-signed**. An artifact attestation is a cryptographically signed assertion binding a SHA-256 artifact digest to its GitHub Actions build provenance. It does not prove runtime safety or that installed browser, Git and editor integration has passed.

## Getting a test EXE

Go to [Releases](https://github.com/HRAshton/git-workspace-pool/releases) and select the newest **Attested Windows testing preview**. Download:

- `GitWorkspacePool.Tray.exe` and `GitWorkspacePool.Tray.exe.sha256` to run the standalone self-contained executable.
- Or `GitWorkspacePool-win-x64-0.1.0-rc.2.zip` and its corresponding `.sha256` to install the protocol handler and userscript through the included installer and read the recovery guidance.
- The accompanying `.sigstore.json` and `.intoto.jsonl` files are portable provenance evidence. Save these alongside the artifacts for offline verification.

The standalone EXE alone **does not register the `prworkspace://` protocol**. To test browser links, follow the ZIP's per-user install steps. Git/VS Code/Windows prerequisites and credentials must be configured on the target PC. Test with disposable clone/workspace locations first and preserve personal data. [Installed acceptance checklist](https://github.com/HRAshton/git-workspace-pool/issues/39).

## Verify downloaded artifacts

On Windows with a current [GitHub CLI](https://cli.github.com/) installed, open PowerShell in your download directory. Confirm the SHA-256 against the `.sha256` file and verify the signed build provenance **before executing** the EXE:

```powershell
(Get-FileHash .\GitWorkspacePool.Tray.exe -Algorithm SHA256).Hash
Get-Content .\GitWorkspacePool.Tray.exe.sha256

gh attestation verify .\GitWorkspacePool.Tray.exe --repo HRAshton/git-workspace-pool --signer-workflow HRAshton/git-workspace-pool/.github/workflows/attest.yml --source-ref refs/heads/main --deny-self-hosted-runners --bundle .\GitWorkspacePool.Tray.exe.sigstore.json

gh attestation verify .\GitWorkspacePool-win-x64-0.1.0-rc.2.zip --repo HRAshton/git-workspace-pool --signer-workflow HRAshton/git-workspace-pool/.github/workflows/attest.yml --source-ref refs/heads/main --deny-self-hosted-runners --bundle .\GitWorkspacePool-win-x64-0.1.0-rc.2.zip.intoto.jsonl
```

Compare the reported artifact source digest with the commit SHA on the GitHub Release page. For stricter verification, add `--source-digest <exact-40-character-release-commit-sha>`. The workflow performs this strict check automatically before publication. `--bundle` uses the downloaded, signed evidence; omitting `--bundle` fetches the live GitHub attestation record.

## Release workflow and trust boundary

`.github/workflows/attest.yml` runs only from trusted `main`, normally by deliberate **Actions → Attested Windows candidate → Run workflow**. It also runs once when the release workflow itself changes on `main` to provide the initial example prerelease. It has two jobs:

1. A Windows job with read-only source permission builds, tests, packages, attests the EXE and ZIP using GitHub's OIDC/Sigstore, verifies both exact subjects and source commit, exports SLSA provenance in JSONL, and uploads a bounded-retention Actions artifact.
2. A separate publishing job downloads *only the artifact from this workflow run*, rechecks SHA-256 and signed attestations, and uses limited `contents: write` permission to create a **prerelease** and attach the signed artifacts. This job does not build or mint attestations.

The `.intoto.jsonl` release asset is generated using `gh attestation download`, then independently verified; the `.sigstore.json` assets are the signed GitHub attestation bundles. These filenames also make release provenance discoverable to the OpenSSF Scorecard **Signed-Releases** check, although a Scorecard score does not independently verify signatures.

No stable release or historical state migration is implied. Before the first stable release, record installed Windows acceptance, real large-repository latency and artifact preservation in issue #39, and plan Windows Authenticode code signing if distribution reputation requires it.
