# Security policy

This project has not had its first release. There are no supported stable versions or security-maintenance guarantees yet.

## Reporting vulnerabilities

Please **do not publish exploit details, credentials, private repository URLs or local paths** in GitHub issues or pull requests.

Use the repository's [private vulnerability reporting](https://github.com/HRAshton/git-workspace-pool/security/advisories/new) if GitHub enables that option. If private reporting is unavailable, open a public issue **without technical details** asking the maintainer to establish a private reporting channel before sharing sensitive information.

Include steps to reproduce and impact privately. Maintainers will coordinate a fix and disclosure if an issue is confirmed.

## Security boundaries

A browser link must never grant repository trust automatically. The application does not run builds or repository scripts during browser-driven open and must preserve unmanaged/dirty worktrees. Report bypasses of origin, filesystem containment, authentication, path, locking or data-preservation protections as security issues.
