# Contributing

Development targets Windows, .NET SDK pinned in `global.json`, Git for Windows and VS Code. Please open an issue for significant changes and submit narrowly scoped pull requests against `main`.

- Run `./scripts/Test-Changes.ps1` in PowerShell 7 on Windows; CI runs Windows build/tests and separate REUSE compliance checks.
- Test Git transitions only in disposable repositories; never experiment on another user's source clone, ignored build cache or workspace. Preserve dirty and interrupted state without destructive cleanup.
- Keep Git CLI invocations noninteractive, bounded, argument-list based and safe against untrusted branch/repo input.
- Retain the project's 0BSD licensing for original contributions. The root `REUSE.toml` documents first-party files; if you add third-party code, data or art, include their actual SPDX and copyright information and required license texts. Do not relicense material you do not own.
- Favor measured browser-to-editor performance on representative Windows repositories. Synthetic Git timings are not end-to-end latency.

Security issues belong in the private reporting path described in [SECURITY.md](SECURITY.md), not in a public PR.
