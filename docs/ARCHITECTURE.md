# Physical warm worktrees

The existing Core, Infrastructure and WinForms Tray projects remain the application.

`OpenGitBranchHandler` validates a v2 browser request against JSON registrations, calls `WarmWorkspacePool`, then starts VS Code. The pool holds a source-clone lock while verifying origin, quarantining interrupted intents and selecting/publishing ownership. An existing assignment is identity-checked and returned without status changes, checkout or fetch. Missing/foreign assignments fail closed.

For a new assignment the Git adapter fetches into a unique application ref and pins a full SHA. Available worktrees are ranked by exact HEAD, optional compatible input fingerprint, actual changed-tree path count and deterministic identity. Up to eight candidates receive final HEAD, registration, status, index flag, operation-marker, unsupported-feature and incoming-path collision checks. Unsafe candidates are retained and quarantined; policy-incompatible donors are left Available. No eligible donor means a new random absent child of the trusted root.

`SqliteWarmWorkspaceRegistry` uses a fresh application-id/versioned database with unique path and repository/branch ownership. Its short writes never pretend Git is transactional. Available -> Transitioning is durable before checkout; verified HEAD/registration/status -> Assigned is published afterward. Interrupted/failing mutation -> Quarantined, with no rollback/reset/cleanup. Preparation uses the same durable creation path and publishes Available. Release checks an unchanged detached assignment before making it Available, preserving ignored bytes in place.

`WarmWorktreeGit` reuses the existing bounded CLI/security adapter. Checkout uses `--detach --no-overwrite-ignore`; Git refusal supplements preflight inspection. No cache recursion/hashing/copying occurs. Literal artifact input files are compared by tree metadata; unsupported or missing policy inputs never mean compatible. Unknown ignored output validity belongs to the build tool.

SQLite stores ownership evidence only. JSON stores trusted clone URL, source clone, workspace root, manual branch names, optional artifact input paths and editor configuration. OS locks are released on process death; database intent survives. No old database schema is opened or migrated. No worker scheduler, leases, generic workflow engine or provider API remains.
