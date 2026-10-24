# Completion work

The active product target is a Windows workspace pool for configured Git repositories and branches, including Bitbucket Data Center 9, using ordinary Git authentication. PR APIs are not required. Existing GitHub PR support remains compatible.

This plan tracks implementation and acceptance separately. An unchecked gate is not complete merely because code or an unsigned package exists.

1. **Shared lifecycle:** migrate existing generic branch records without moving files; connect suspension, snapshots, parking, retained reopening, released-slot allocation, artifact transfer and recovery. Prove legacy migration and cross-branch data preservation.
2. **Daily operation:** repository/editor settings UI, cancellable progress, local diagnostics and timing evidence; configured baseline refresh with bounded work and no project execution.
3. **Pool and storage:** verified candidate collection, opt-in selection, artifact compatibility policies and storage budgets. Automatic deletion remains prohibited until every disposal and recovery gate is satisfied; read-only inventory is not deletion authorization.
4. **Recovery and release hardening:** filesystem/corruption/fault coverage, repair that preserves original evidence, extended crash tests, reproducible packaging and explicit version/backup/rollback evidence.
5. **Acceptance:** installed Windows browser/tray/VS Code and company authentication, representative project latency/disk measurements, real low-disk/AV behavior, and signing with the owner's certificate. Record source commit, package hash and evidence. These require the actual environment or signing identity; synthetic/offline tests do not replace them.

Optional ReFS acceleration and provider-specific API adapters remain extension work unless needed to satisfy the active workflow. Do not mark those implemented when generic Git provides the requested workflow.

## Progress

- [x] Shared branch/PR lifecycle and upgrade tests merged after current-head Windows validation.
- [x] Settings, cancellation/progress and opt-in local timing diagnostics merged after current-head Windows validation.
- [x] Verified candidate collection/ranking, commit-bound user selection and existing copy-only npm policy implemented; broader artifact classes and measured automatic reuse remain gated.
- [ ] Storage governance and safe recovery/retention policy.
- [ ] Extended release validation and package evidence.
- [ ] Installed environment acceptance and signing.
