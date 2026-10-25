# In-place rewrite status

Implemented: minimal physical worktree ownership, isolated schema, repo locks, durable intent/quarantine, safe detached transitions, explicit release/preparation, tree-delta ranking, optional literal artifact-input fingerprints, browser/tray integration and removal of snapshots/parking/slots/copying/background scheduling.

Validation in progress: Windows PR CI, disposable integration/crash tests and synthetic timing distributions. See [evidence](docs/REWRITE_EVIDENCE.md) for actual results and limitations.

Remaining acceptance work:

- Measure representative real repositories, repeated fresh/warm/reopen median and p95, browser request to ready and click to visible editor on Windows.
- Real process-kill tests now include actual mid-checkout interruption with a retained index.lock.
- Perform installed browser/tray/VS Code acceptance with HTTPS and SSH authentication on a target Windows machine.
- Investigate measured bottlenecks before adding prediction, weighting or automation. No cache-copying or background warm-up is planned for v1.

This is unreleased development software. Legacy data preservation remains mandatory.
