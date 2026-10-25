# Verify a candidate before installation

The RC requires build provenance attestation; Authenticode/MSI signing is not a gate. The PE executable remains unsigned. This policy is for the current Git-only RC, not a claim of broad OpenSSF certification or a particular SLSA level.

Only `.github/workflows/attest.yml` on `refs/heads/main` produces accepted candidate attestations. It runs the tests and package/backup checks first, then uses the commit-pinned official `actions/attest` action to bind the ZIP SHA-256 to a SLSA provenance predicate in an in-toto statement, with GitHub Actions OIDC/Sigstore identity. PR workflows never receive this candidate publication identity. A workflow file being present is not proof that a successful attestation exists; require a successful trusted-main run and verification.

Download the candidate ZIP, adjacent checksum, package manifest and attestation bundle from that same successful run. Before extracting or running scripts, use a current GitHub CLI:

```powershell
gh attestation verify .\GitWorkspacePool-win-x64-0.1.0-rc.2.zip --repo HRAshton/git-workspace-pool --signer-workflow HRAshton/git-workspace-pool/.github/workflows/attest.yml --source-ref refs/heads/main --source-digest <EXPECTED_SOURCE_COMMIT> --deny-self-hosted-runners
```

Replace `<EXPECTED_SOURCE_COMMIT>` with the full source commit shown in the trusted-main workflow run. Do not copy an untrusted value from another archive. Verification checks the artifact digest, provenance type and identity; the default predicate type is `https://slsa.dev/provenance/v1`. You can supply `--bundle <downloaded-bundle-file>` for a saved bundle, subject to GitHub CLI's documented offline trust-root requirements. Archive the verification result with acceptance evidence.

Then compare the ZIP checksum and inspect `PACKAGE_MANIFEST.json` source commit/tree against that run. Extract to a new directory and run `Test-PackageIntegrity.ps1` to verify its bounded full file inventory. Only then run `Install-PerUser.ps1`.

The manifest and `.sha256` are integrity/provenance metadata, not independent publisher authentication. Attestation establishes the build's identity and source; it does not certify bug-free code, safe project contents, Windows SmartScreen reputation or company Git credentials.

Authoritative format/verification references: [actions/attest](https://github.com/actions/attest), [in-toto statement](https://github.com/in-toto/attestation/tree/main/spec), [SLSA provenance v1](https://slsa.dev/provenance/v1), [GitHub CLI verification](https://cli.github.com/manual/gh_attestation_verify).
