# Git-only RC acceptance record

Use this checklist for the exact attested candidate ZIP. Certificate signing is not required at this step. Record source commit/tree, trusted-main workflow URL, ZIP SHA-256, attestation verification, Windows/Git/VS Code/browser versions and results without sharing secrets.

- [ ] Current-head PR CI and extended crash/package/backup checks pass with no unresolved blocking findings.
- [ ] The trusted-main Attested Windows candidate run passes, and repository/workflow/ref/source-commit/digest attestation verification succeeds. Retain its bundle, manifest and checksum.
- [ ] Complete `BITBUCKET_ACCEPTANCE.md` on the target machine, including SSH and/or HTTPS as actually used.
- [ ] Verify unknown-repo registration requires consent, cancelled registration writes nothing, confirmed create-bare and existing-normal/bare paths work, and wrong origins/occupied destinations are refused.
- [ ] Verify `^master$` default and `^20\d\d[rR]\d{3}$` production regex select actual remote heads; empty patterns disable background work; failures back off without changing checked-out files.
- [ ] Verify grouped tray menus and icon, keyboard/dialog/cancel behavior and default `code.exe` resolution with the actual VS Code installation.
- [ ] Verify reopen/restart preserves staged/unstaged/untracked/ignored content and local commits. Verify snapshot/isolated restore, suspend, park/unpark, explicit release/retained open and optional npm copy on disposable data.
- [ ] Verify actual previous-profile upgrade and matching-state rollback using full closed backups, including source Git metadata, worktrees, snapshots and parked folders. Do not downgrade against a newer database.
- [ ] Verify locked/denied/replaced paths, unavailable network, cancellation, actual low storage and real AV behavior preserve originals/evidence and report failure clearly.
- [ ] Record representative latency/storage results and accept the explicit RC limitations. No automatic cleanup, disposal or project execution is authorized.
- [ ] Release owner approves this exact candidate and archives the evidence. No universal OpenSSF/SLSA level, security certification or signed-binary claim is made.

If a mandatory gate is unverified, the ZIP is an engineering candidate for testing, not an accepted RC release. Broader historical roadmap extensions do not become implemented when this checklist passes.
