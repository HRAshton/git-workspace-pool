# Exclusive slot ownership and safe release (design gate)

**Status:** architecture proposal for M2/M3, not implemented. No automatic release or reuse is enabled by this document.

## Why parking is not release

A PR session currently reserves `sessions.workspace_path` for its entire lifetime, even when
its worktree is moved to `parking_path`. The session may be suspended or parked, but a unique
database reservation still protects the original path. Simply deleting that unique constraint,
rewriting `workspace_path`, or treating an empty folder as free would risk one PR overwriting
another's worktree.

**Hard separation:** a *session* represents the user's PR data and recovery history; a *slot*
represents a reusable Git worktree location; a *lease* represents the exclusive right to mutate
a slot during one allocation generation. Parking preserves session data. Releasing a slot must
not imply the parked session can be deleted.

## Proposed module boundaries

| Module | Owns | Must not do |
| --- | --- | --- |
| Session lifecycle | PR identity, user changes, Active / Parking / Parked / RecoveryRequired | Decide Git checkout implementation or disk eviction |
| Workspace slot pool | stable slot identity, path, generation, Ready / Reserved / InUse / RecoveryRequired | Interpret provider URLs or delete PR data |
| Exclusive lease coordinator | compare-and-swap generation, intent/outcome journal and interprocess serialization | Assume a directory is free from its name |
| Worktree mechanism | registered Git moves, commit verification, filesystem reparse-point checks | Change session/lease ownership on its own |
| Snapshot/recovery | durable manifests, exact recovery and independent audit receipts | Authorize recycling merely because a snapshot exists |
| Cost policy | ranked, immutable, bounded, measured candidate list | Perform side effects or confer ownership |
| Tray/UX | explicit user consent and status reporting | Manipulate database/file state directly |

Keep all state mutations in application use cases under narrowly scoped ports. Infrastructure
adapters implement Git, SQLite, locks and filesystem operations; the tray only composes them.

## Proposed persisted ownership (requires a versioned schema migration)

- `workspace_slots`: `slot_id` primary key, repository identity, canonical absolute slot
  path (unique), immutable source clone, state, nonnegative generation and last verified HEAD.
- `slot_leases`: `lease_id`, `slot_id`, `session_id`, `generation`, state and durable
  timestamps. A partial unique constraint protects at most one live lease per slot and at
  most one live slot lease per session.
- `sessions`: keep the immutable session ID, original requested PR, pinned HEAD, durable
  parking/recovery ownership and historical audit trail. A *preferred* slot path is not a
  claim on it once explicitly released.
- `operations`: durable intent and verified outcome for moving the tree, releasing a slot,
  reserving a slot and transferring ownership. Store the expected generation, original Git
  registration and target paths; do not infer them from directories.
- Migration from v5 must preserve *all* existing session, snapshot, restore and parking
  references, and treat existing session workspace paths as leased/protected by default.
  Never mark them automatically ready on migration.

This is a target schema contract, not a migration script. First implement and test a
transactional path/lease registry and its upgrade before changing the current unique
workspace-path rule.

## Required invariants

1. **One mutator per slot:** a successful lease reservation atomically advances the slot's
   generation. Stale requests with an old generation are rejected, including from another
   tray process. SQLite and an OS lock cover the critical physical operations.
2. **No ownership from emptiness:** a missing directory, free-looking name, process exit or
   snapshot receipt cannot grant a lease. Path aliases, nested paths, symlinks/junctions and
   case-insensitive collisions are rejected at the reservation boundary.
3. **No dirty recycling:** any tracked, staged, unstaged, untracked or relevant ignored content
   that is not demonstrably pool-owned forbids checkout reuse. All unverified data goes to
   protected parking or `RecoveryRequired`.
4. **No silent movement:** the user explicitly requests release; the app never infers it from
   closing VS Code. An editor lock or unsupported submodule must fail closed.
5. **First preserve, then release:** complete and verify the Git move into an owned parked
   location, journal its ownership, verify the old path and Git registry are free, and *only
   then* commit the slot lease's released generation.
6. **Never restore into an occupied slot:** reopening a parked PR must obtain a fresh exclusive
   lease or decline; it must not move back onto a reused former slot.
7. **Crash recovery:** after restart inspect the durable operation intent, both paths, Git's
   worktree registry and the expected generation. Roll forward only when unambiguous; else
   quarantine all affected state, preserve every file and surface diagnostics.
8. **Disk reclamation is distinct:** release changes ownership, not file retention. Eviction
   needs its own audited transaction and is forbidden for any active, parked, orphaned,
   unknown or recovery-needed data.

## Safe end-to-end transition

1. User selects **Release PR slot** and confirms that the source worktree will be parked.
2. Acquire the global slot lease transaction and the session's deterministic OS lock; pin
   the exact session ID, slot ID and generation.
3. Verify Git registration, source remote identity, both paths and all user data before
   altering Git. Refuse unsafe moves and unsupported layouts.
4. Commit a `ReleasePending` intent. Move the *whole linked worktree* to its distinct owned
   parked location using Git; do not clean/reset it.
5. Verify the moved tree and actual Git registration, journal ownership of the parked tree,
   verify that the old location and its registration are both absent.
6. Commit `slot_leases.Released` and transition the slot to `Ready` with a new generation.
   If either side is ambiguous, mark `RecoveryRequired` and leave the files alone.
7. Only a separately journalled `AcquireSlot` use case may reserve the next generation.
   Recheck the slot's physical state before any checkout or artifact operation.

A warm slot may still be *empty* after release; warm preparation, artifact reuse and checkout
are separate policy-gated use cases. Cost ranking cannot authorize a write.

## Acceptance/fault matrix before enabling reuse

- Two processes concurrently release/acquire the same slot; stale-generation CAS fails.
- A user reopens the parked PR after its original path was legitimately reused.
- Kill the process after each intent, Git move, verification and commit; restart reconciles
  without deleting user data or double-leasing the slot.
- Dirty tracked/index/untracked/ignored/binary changes survive parking and later restoration
  byte-for-byte. No hardlinks to writable artifacts.
- Occupied/reparse-point/nested paths, Git submodules, locked files and unexpected clone
  registrations cause non-destructive refusal.
- Legacy v5 migration preserves all leases as protected and all historical snapshot receipts.
- Disk exhaustion and non-atomic filesystem failures retain durable recovery evidence.
- Benchmarks compare cold worktree creation to validated warm reuse; record p50/p95 time to
  editor and physical disk delta. No performance promise based only on Git ancestry.

**Implementation order:** protected slot registry → generation-aware lease/operation journal →
read-only inventory and verification → explicit non-destructive release → crash tests →
verified warm allocation → artifact copy policies → bounded eviction. Automatic recycling must
stay disabled until every safety gate passes.
