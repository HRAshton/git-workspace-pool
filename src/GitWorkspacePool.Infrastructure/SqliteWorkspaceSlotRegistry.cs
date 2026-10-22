using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Transactional slot compare-and-swap. Slot readiness must be established by a
/// separate, not-yet-enabled verified release workflow.
/// </summary>
public sealed class SqliteWorkspaceSlotRegistry : IWorkspaceSlotRegistry, IWorkspaceSlotOwnershipAuditor, IWorkspaceSlotReleasePreparationJournal, IWorkspaceSlotReleaseCommitJournal, IWorkspaceReadySlotAllocator, IWorkspaceSlotReservationInventory
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes the existing state database, including protective legacy backfill.
    /// </summary>
    public SqliteWorkspaceSlotRegistry(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _ = new SqliteWorkspaceSessionJournal(databasePath);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        };
        _connectionString = builder.ToString();
    }

    /// <inheritdoc/>
    public Task<WorkspaceSlot?> FindSlotAsync(
        Guid slotId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT slot_id, repository_key, source_path, workspace_path,
                   generation, state, head_commit
              FROM workspace_slots
             WHERE slot_id = $slot
            """;
        command.Parameters.AddWithValue("$slot", slotId.ToString("N"));

        using SqliteDataReader reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadSlot(reader) : null);
    }

    /// <inheritdoc/>
    public Task<WorkspaceSlot?> FindHeldSlotAsync(
        Guid sessionId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT slots.slot_id, slots.repository_key, slots.source_path,
                   slots.workspace_path, slots.generation, slots.state, slots.head_commit
              FROM workspace_slots AS slots
              JOIN workspace_slot_leases AS leases ON leases.slot_id = slots.slot_id
             WHERE leases.session_id = $session AND leases.state = 'Held'
               AND leases.generation = slots.generation
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        using SqliteDataReader reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadSlot(reader) : null);
    }

    /// <inheritdoc/>
    public Task<WorkspaceSlotLease?> TryLeaseReadyAsync(
        Guid slotId,
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (slotId == Guid.Empty || sessionId == Guid.Empty || expectedGeneration < 0)
        {
            throw new ArgumentException("A valid slot, session and nonnegative generation are required.");
        }

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand reserve = connection.CreateCommand();
        reserve.Transaction = transaction;
        reserve.CommandText = """
            UPDATE workspace_slots
               SET generation = generation + 1,
                   state = 'Reserved',
                   updated_utc = $utc
             WHERE slot_id = $slot
               AND generation = $expected
               AND state = 'Ready'
               AND generation < 9223372036854775807
               AND EXISTS (
                   SELECT 1 FROM sessions AS owner
                    WHERE owner.operation_id = $session
                      AND owner.state = 'Preparing'
                      AND owner.repository_key = workspace_slots.repository_key
                      AND owner.source_path = workspace_slots.source_path
                      AND owner.workspace_path = workspace_slots.workspace_path
               )
               AND NOT EXISTS (
                   SELECT 1 FROM workspace_slot_leases AS lease
                    WHERE lease.session_id = $session AND lease.state = 'Held'
               )
               AND NOT EXISTS (
                   SELECT 1 FROM workspace_slot_leases AS lease
                    WHERE lease.slot_id = $slot AND lease.state = 'Held'
               )
            """;
        reserve.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        reserve.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        reserve.Parameters.AddWithValue("$expected", expectedGeneration);
        reserve.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));

        if (reserve.ExecuteNonQuery() != 1)
        {
            // Disposal rolls back the transaction without changing the slot.
            return Task.FromResult<WorkspaceSlotLease?>(null);
        }

        WorkspaceSlotLease lease = new(
            Guid.NewGuid(),
            slotId,
            sessionId,
            checked(expectedGeneration + 1)
        );
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO workspace_slot_leases(
                lease_id, slot_id, session_id, generation, state, created_utc
            ) VALUES ($lease, $slot, $session, $generation, 'Held', $utc)
            """;
        insert.Parameters.AddWithValue("$lease", lease.LeaseId.ToString("N"));
        insert.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        insert.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        insert.Parameters.AddWithValue("$generation", lease.Generation);
        insert.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return Task.FromResult<WorkspaceSlotLease?>(lease);
    }

    /// <inheritdoc/>
    public Task<WorkspaceReadySlotReservation?> FindReservedAsync(
        Guid slotId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner.operation_id, owner.repository_key, owner.pr_number,
                   owner.source_path, owner.workspace_path, owner.state,
                   owner.pinned_commit, owner.parking_path, owner.parking_head,
                   lease.lease_id, lease.generation
              FROM workspace_slots AS slot
              JOIN workspace_slot_leases AS lease
                ON lease.slot_id = slot.slot_id AND lease.state = 'Held'
               AND lease.generation = slot.generation
              JOIN sessions AS owner
                ON owner.operation_id = lease.session_id
             WHERE slot.slot_id = $slot
               AND slot.state = 'Reserved'
               AND owner.state = 'Preparing'
               AND owner.workspace_path = slot.workspace_path
               AND owner.source_path = slot.source_path
               AND owner.repository_key = slot.repository_key
            """;
        command.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<WorkspaceReadySlotReservation?>(null);
        }

        WorkspaceSession session = new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            WorkspaceSessionState.Preparing,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8)
        );
        WorkspaceSlotLease lease = new(
            Guid.Parse(reader.GetString(9)),
            slotId,
            session.OperationId,
            reader.GetInt64(10)
        );
        return Task.FromResult<WorkspaceReadySlotReservation?>(
            new WorkspaceReadySlotReservation(session, lease)
        );
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WorkspaceReadySlotReservation>> ListReservedAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner.operation_id, owner.repository_key, owner.pr_number,
                   owner.source_path, owner.workspace_path, owner.pinned_commit,
                   owner.parking_path, owner.parking_head,
                   lease.lease_id, lease.slot_id, lease.generation
              FROM workspace_slots AS slot
              JOIN workspace_slot_leases AS lease
                ON lease.slot_id = slot.slot_id
               AND lease.state = 'Held'
               AND lease.generation = slot.generation
              JOIN sessions AS owner ON owner.operation_id = lease.session_id
             WHERE slot.state = 'Reserved'
               AND owner.state = 'Preparing'
               AND owner.repository_key = slot.repository_key
               AND owner.source_path = slot.source_path
               AND owner.workspace_path = slot.workspace_path
             ORDER BY slot.rowid
             LIMIT 10001
            """;
        List<WorkspaceReadySlotReservation> pending = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.Count >= 10000)
            {
                throw new InvalidOperationException(
                    "Interrupted slot allocation inventory exceeds the safety limit."
                );
            }

            WorkspaceSession session = new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                WorkspaceSessionState.Preparing,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)
            );
            WorkspaceSlotLease lease = new(
                Guid.Parse(reader.GetString(8)),
                Guid.Parse(reader.GetString(9)),
                session.OperationId,
                reader.GetInt64(10)
            );
            pending.Add(new WorkspaceReadySlotReservation(session, lease));
        }

        return Task.FromResult<IReadOnlyList<WorkspaceReadySlotReservation>>(pending);
    }

    /// <inheritdoc/>
    public Task<WorkspaceReadySlotReservation?> TryReserveAsync(
        Guid slotId,
        long expectedGeneration,
        string repositoryKey,
        int pullRequestNumber,
        string sourcePath,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (
            slotId == Guid.Empty
            || expectedGeneration < 0
            || string.IsNullOrWhiteSpace(repositoryKey)
            || pullRequestNumber <= 0
        )
        {
            throw new ArgumentException("A valid slot generation and PR are required.");
        }

        string source = Path.GetFullPath(sourcePath);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        WorkspaceSlot slot;
        using (SqliteCommand find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT slot_id, repository_key, source_path, workspace_path,
                       generation, state, head_commit
                  FROM workspace_slots WHERE slot_id = $slot
                """;
            find.Parameters.AddWithValue("$slot", slotId.ToString("N"));
            using SqliteDataReader reader = find.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<WorkspaceReadySlotReservation?>(null);
            }

            slot = ReadSlot(reader);
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            slot.State != WorkspaceSlotState.Ready
            || slot.Generation != expectedGeneration
            || !slot.RepositoryKey.Equals(repositoryKey, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(slot.SourcePath).Equals(source, comparison)
        )
        {
            return Task.FromResult<WorkspaceReadySlotReservation?>(null);
        }

        // The old Released owner's historical path may be reused, but every
        // live original path and every parked path remains reserved. Reject
        // nested/alias collisions, not only equal SQL UNIQUE values.
        using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT repository_key, pr_number, workspace_path,
                       parking_path, state FROM sessions
                """;
            using SqliteDataReader reader = existing.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    reader.GetString(0).Equals(repositoryKey, StringComparison.OrdinalIgnoreCase)
                    && reader.GetInt32(1) == pullRequestNumber
                )
                {
                    return Task.FromResult<WorkspaceReadySlotReservation?>(null);
                }

                if (
                    (reader.GetString(4) != "Released"
                        && OverlapsSlotPaths(slot.WorkspacePath, reader.GetString(2)))
                    || (!reader.IsDBNull(3)
                        && OverlapsSlotPaths(slot.WorkspacePath, reader.GetString(3)))
                )
                {
                    return Task.FromResult<WorkspaceReadySlotReservation?>(null);
                }
            }
        }

        using (SqliteCommand ready = connection.CreateCommand())
        {
            ready.Transaction = transaction;
            ready.CommandText = """
                UPDATE workspace_slots
                   SET state = 'Reserved', generation = generation + 1,
                       updated_utc = $utc
                 WHERE slot_id = $slot AND state = 'Ready'
                   AND generation = $generation
                   AND generation < 9223372036854775807
                   AND NOT EXISTS (
                       SELECT 1 FROM workspace_slot_leases
                        WHERE slot_id = $slot AND state = 'Held'
                   )
                """;
            ready.Parameters.AddWithValue("$slot", slotId.ToString("N"));
            ready.Parameters.AddWithValue("$generation", expectedGeneration);
            ready.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            if (ready.ExecuteNonQuery() != 1)
            {
                return Task.FromResult<WorkspaceReadySlotReservation?>(null);
            }
        }

        WorkspaceSession session = new(
            Guid.NewGuid(),
            repositoryKey,
            pullRequestNumber,
            source,
            slot.WorkspacePath,
            WorkspaceSessionState.Preparing,
            null
        );
        WorkspaceSlotLease lease = new(
            Guid.NewGuid(), slotId, session.OperationId, checked(expectedGeneration + 1)
        );
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sessions (
                    operation_id, repository_key, pr_number, source_path,
                    workspace_path, state, pinned_commit, updated_utc
                ) VALUES ($session, $repository, $pr, $source, $path,
                          'Preparing', NULL, $utc);

                INSERT INTO workspace_slot_leases (
                    lease_id, slot_id, session_id, generation, state, created_utc
                ) VALUES ($lease, $slot, $session, $generation, 'Held', $utc);

                INSERT INTO session_events(operation_id, kind, created_utc)
                VALUES ($session, 'ReleasedSlotReserved', $utc);
                """;
            insert.Parameters.AddWithValue("$session", session.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$repository", repositoryKey);
            insert.Parameters.AddWithValue("$pr", pullRequestNumber);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$path", slot.WorkspacePath);
            insert.Parameters.AddWithValue("$slot", slotId.ToString("N"));
            insert.Parameters.AddWithValue("$lease", lease.LeaseId.ToString("N"));
            insert.Parameters.AddWithValue("$generation", lease.Generation);
            insert.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.FromResult<WorkspaceReadySlotReservation?>(
            new WorkspaceReadySlotReservation(session, lease)
        );
    }

    /// <inheritdoc/>
    public Task<bool> TryActivateAsync(
        WorkspaceReadySlotReservation reservation,
        string commitSha,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (
            reservation.Lease.Generation <= 0
            || commitSha.Length is not (40 or 64)
            || !commitSha.All(Uri.IsHexDigit)
        )
        {
            throw new ArgumentException("A valid reservation and full Git commit are required.");
        }

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand protect = connection.CreateCommand())
        {
            protect.Transaction = transaction;
            protect.CommandText = """
                UPDATE workspace_slots
                   SET state = 'Protected', head_commit = $commit,
                       updated_utc = $utc
                 WHERE slot_id = $slot AND state = 'Reserved'
                   AND generation = $generation
                   AND EXISTS (
                       SELECT 1 FROM workspace_slot_leases AS lease
                       JOIN sessions AS session
                         ON session.operation_id = lease.session_id
                      WHERE lease.lease_id = $lease
                        AND lease.slot_id = workspace_slots.slot_id
                        AND lease.session_id = $session
                        AND lease.generation = workspace_slots.generation
                        AND lease.state = 'Held'
                        AND session.state = 'Preparing'
                        AND session.pinned_commit = $commit
                        AND session.workspace_path = workspace_slots.workspace_path
                        AND session.source_path = workspace_slots.source_path
                        AND session.repository_key = workspace_slots.repository_key
                   )
                """;
            protect.Parameters.AddWithValue("$slot", reservation.Lease.SlotId.ToString("N"));
            protect.Parameters.AddWithValue("$lease", reservation.Lease.LeaseId.ToString("N"));
            protect.Parameters.AddWithValue("$session", reservation.Session.OperationId.ToString("D"));
            protect.Parameters.AddWithValue("$generation", reservation.Lease.Generation);
            protect.Parameters.AddWithValue("$commit", commitSha.ToLowerInvariant());
            protect.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            if (protect.ExecuteNonQuery() != 1)
            {
                return Task.FromResult(false);
            }
        }

        using (SqliteCommand active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = """
                UPDATE sessions SET state = 'Active', updated_utc = $utc
                 WHERE operation_id = $session
                   AND state = 'Preparing' AND pinned_commit = $commit
                """;
            active.Parameters.AddWithValue("$session", reservation.Session.OperationId.ToString("D"));
            active.Parameters.AddWithValue("$commit", commitSha.ToLowerInvariant());
            active.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            if (active.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Released slot activation owner CAS failed.");
            }
        }

        using (SqliteCommand audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO session_events(operation_id, kind, created_utc)
                VALUES ($session, 'ReleasedSlotActivated', $utc)
                """;
            audit.Parameters.AddWithValue("$session", reservation.Session.OperationId.ToString("D"));
            audit.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.FromResult(true);
    }

    private static bool OverlapsSlotPaths(string left, string right)
    {
        string a = Path.GetFullPath(left).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
        );
        string b = Path.GetFullPath(right).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return a.Equals(b, comparison)
            || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WorkspaceSlotIntegrityFinding>> AuditAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT slots.slot_id, slots.workspace_path, slots.state,
                   slots.generation, slots.repository_key, slots.source_path,
                   leases.session_id, leases.generation,
                   owners.repository_key, owners.source_path, owners.workspace_path
              FROM workspace_slots AS slots
              LEFT JOIN workspace_slot_leases AS leases
                ON leases.slot_id = slots.slot_id AND leases.state = 'Held'
              LEFT JOIN sessions AS owners
                ON owners.operation_id = leases.session_id
             ORDER BY slots.rowid
             LIMIT 10001
            """;

        List<WorkspaceSlotIntegrityFinding> findings = [];
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++count > 10000)
            {
                throw new InvalidOperationException(
                    "The slot ownership inventory exceeds the safe inspection limit."
                );
            }

            Guid slotId = Guid.Parse(reader.GetString(0));
            string path = reader.GetString(1);
            WorkspaceSlotState state = Enum.Parse<WorkspaceSlotState>(
                reader.GetString(2), ignoreCase: false
            );
            long generation = reader.GetInt64(3);
            bool hasLease = !reader.IsDBNull(6);
            Guid? sessionId = hasLease ? Guid.Parse(reader.GetString(6)) : null;

            void Report(WorkspaceSlotIntegrityIssue issue) =>
                findings.Add(new WorkspaceSlotIntegrityFinding(slotId, path, issue, sessionId));

            if (state == WorkspaceSlotState.Protected && !hasLease)
            {
                Report(WorkspaceSlotIntegrityIssue.ProtectedLeaseMissing);
            }
            else if (state == WorkspaceSlotState.Ready && hasLease)
            {
                Report(WorkspaceSlotIntegrityIssue.ReadyWithHeldLease);
            }
            else if (state == WorkspaceSlotState.Reserved && !hasLease)
            {
                Report(WorkspaceSlotIntegrityIssue.ReservedLeaseMissing);
            }

            if (!hasLease)
            {
                continue;
            }

            if (reader.GetInt64(7) != generation)
            {
                Report(WorkspaceSlotIntegrityIssue.LeaseGenerationMismatch);
            }

            StringComparison paths = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (
                reader.IsDBNull(8)
                || !reader.GetString(4).Equals(
                    reader.GetString(8), StringComparison.OrdinalIgnoreCase
                )
                || !Path.GetFullPath(reader.GetString(5)).Equals(
                    Path.GetFullPath(reader.GetString(9)), paths
                )
                || !Path.GetFullPath(path).Equals(
                    Path.GetFullPath(reader.GetString(10)), paths
                )
            )
            {
                Report(WorkspaceSlotIntegrityIssue.LeaseOwnerMismatch);
            }
        }

        return Task.FromResult<IReadOnlyList<WorkspaceSlotIntegrityFinding>>(findings);
    }

    /// <inheritdoc/>
    public Task<WorkspaceSlotReleasePreparation?> TryPrepareAsync(
        Guid sessionId,
        Guid slotId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || slotId == Guid.Empty || expectedGeneration < 0)
        {
            throw new ArgumentException("A valid owner, slot and generation are required.");
        }

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT prepared.preparation_id, prepared.slot_id,
                   prepared.session_id, prepared.generation, prepared.state,
                   EXISTS (
                       SELECT 1 FROM workspace_slots AS slots
                       JOIN workspace_slot_leases AS leases
                         ON leases.slot_id = slots.slot_id AND leases.state = 'Held'
                       JOIN sessions AS owners
                         ON owners.operation_id = leases.session_id
                      WHERE slots.slot_id = prepared.slot_id
                        AND slots.generation = prepared.generation
                        AND slots.state = 'Protected'
                        AND leases.generation = prepared.generation
                        AND owners.operation_id = prepared.session_id
                        AND owners.state = 'Parked'
                        AND owners.parking_path IS NOT NULL
                        AND owners.repository_key = slots.repository_key
                        AND owners.source_path = slots.source_path
                        AND owners.workspace_path = slots.workspace_path
                   )
              FROM workspace_slot_release_preparations AS prepared
             WHERE prepared.session_id = $session AND prepared.state = 'Prepared'
            """;
        existing.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                WorkspaceSlotReleasePreparation saved = ReadPreparation(reader);
                return Task.FromResult<WorkspaceSlotReleasePreparation?>(
                    saved.SlotId == slotId
                        && saved.Generation == expectedGeneration
                        && reader.GetInt64(5) == 1
                        ? saved
                        : null
                );
            }
        }

        Guid preparationId = Guid.NewGuid();
        using SqliteCommand prepare = connection.CreateCommand();
        prepare.Transaction = transaction;
        prepare.CommandText = """
            INSERT INTO workspace_slot_release_preparations(
                preparation_id, slot_id, session_id, generation,
                state, created_utc, updated_utc
            )
            SELECT $preparation, slots.slot_id, owners.operation_id,
                   slots.generation, 'Prepared', $utc, $utc
              FROM workspace_slots AS slots
              JOIN workspace_slot_leases AS leases
                ON leases.slot_id = slots.slot_id AND leases.state = 'Held'
              JOIN sessions AS owners
                ON owners.operation_id = leases.session_id
             WHERE slots.slot_id = $slot
               AND owners.operation_id = $session
               AND owners.state = 'Parked'
               AND owners.parking_path IS NOT NULL
               AND slots.state = 'Protected'
               AND slots.generation = $generation
               AND leases.generation = slots.generation
               AND owners.repository_key = slots.repository_key
               AND owners.source_path = slots.source_path
               AND owners.workspace_path = slots.workspace_path
               AND NOT EXISTS (
                   SELECT 1 FROM workspace_slot_release_preparations AS active
                    WHERE active.slot_id = slots.slot_id AND active.state = 'Prepared'
               )
            """;
        prepare.Parameters.AddWithValue("$preparation", preparationId.ToString("N"));
        prepare.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        prepare.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        prepare.Parameters.AddWithValue("$generation", expectedGeneration);
        prepare.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));

        if (prepare.ExecuteNonQuery() != 1)
        {
            // Disposal rolls back any uncommitted journal changes.
            return Task.FromResult<WorkspaceSlotReleasePreparation?>(null);
        }

        transaction.Commit();
        return Task.FromResult<WorkspaceSlotReleasePreparation?>(
            new WorkspaceSlotReleasePreparation(
                preparationId,
                slotId,
                sessionId,
                expectedGeneration,
                WorkspaceSlotReleasePreparationState.Prepared
            )
        );
    }

    /// <inheritdoc/>
    public Task<WorkspaceSlotReleasePreparation?> FindPreparationAsync(
        Guid preparationId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT preparation_id, slot_id, session_id, generation, state
              FROM workspace_slot_release_preparations
             WHERE preparation_id = $preparation
            """;
        command.Parameters.AddWithValue("$preparation", preparationId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadPreparation(reader) : null);
    }

    /// <inheritdoc/>
    public Task<bool> TryCommitPreparedAsync(
        Guid preparationId,
        Guid slotId,
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        // Filesystem and Git were checked under the OS lock. Recheck SQLite
        // identity, state and generation in one atomic, rollback-safe handoff.
        using SqliteCommand ready = connection.CreateCommand();
        ready.Transaction = transaction;
        ready.CommandText = """
            UPDATE workspace_slots
               SET state = 'Ready', head_commit = NULL, updated_utc = $utc
             WHERE slot_id = $slot
               AND state = 'Protected'
               AND generation = $generation
               AND EXISTS (
                   SELECT 1 FROM workspace_slot_release_preparations AS prep
                   JOIN workspace_slot_leases AS lease
                     ON lease.slot_id = prep.slot_id
                    AND lease.state = 'Held'
                    AND lease.session_id = prep.session_id
                    AND lease.generation = prep.generation
                   JOIN sessions AS owner
                     ON owner.operation_id = prep.session_id
                  WHERE prep.preparation_id = $preparation
                    AND prep.state = 'Prepared'
                    AND prep.slot_id = workspace_slots.slot_id
                    AND prep.session_id = $session
                    AND prep.generation = $generation
                    AND owner.state = 'Parked'
                    AND owner.parking_path IS NOT NULL
                    AND owner.parking_head IS NOT NULL
                    AND owner.repository_key = workspace_slots.repository_key
                    AND owner.source_path = workspace_slots.source_path
                    AND owner.workspace_path = workspace_slots.workspace_path
               )
            """;
        ready.Parameters.AddWithValue("$preparation", preparationId.ToString("N"));
        ready.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        ready.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        ready.Parameters.AddWithValue("$generation", expectedGeneration);
        ready.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (ready.ExecuteNonQuery() != 1)
        {
            return Task.FromResult(false);
        }

        using SqliteCommand release = connection.CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            UPDATE workspace_slot_leases
               SET state = 'Released'
             WHERE slot_id = $slot
               AND session_id = $session
               AND generation = $generation
               AND state = 'Held'
            """;
        release.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        release.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        release.Parameters.AddWithValue("$generation", expectedGeneration);
        if (release.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Release lease CAS mismatch.");
        }

        using SqliteCommand park = connection.CreateCommand();
        park.Transaction = transaction;
        park.CommandText = """
            UPDATE sessions
               SET state = 'Released', updated_utc = $utc
             WHERE operation_id = $session AND state = 'Parked'
               AND parking_path IS NOT NULL AND parking_head IS NOT NULL
            """;
        park.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        park.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (park.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Release owner CAS mismatch.");
        }

        using SqliteCommand done = connection.CreateCommand();
        done.Transaction = transaction;
        done.CommandText = """
            UPDATE workspace_slot_release_preparations
               SET state = 'Committed', updated_utc = $utc
             WHERE preparation_id = $preparation
               AND slot_id = $slot
               AND session_id = $session
               AND generation = $generation
               AND state = 'Prepared'
            """;
        done.Parameters.AddWithValue("$preparation", preparationId.ToString("N"));
        done.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        done.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        done.Parameters.AddWithValue("$generation", expectedGeneration);
        done.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (done.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Release preparation CAS mismatch.");
        }

        using SqliteCommand audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO session_events(operation_id, kind, created_utc)
            VALUES ($session, 'SlotReleased', $utc)
            """;
        audit.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        audit.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        audit.ExecuteNonQuery();
        transaction.Commit();
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> CancelAsync(Guid preparationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workspace_slot_release_preparations
               SET state = 'Cancelled', updated_utc = $utc
             WHERE preparation_id = $id AND state = 'Prepared'
            """;
        command.Parameters.AddWithValue("$id", preparationId.ToString("N"));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        bool cancelled = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return Task.FromResult(cancelled);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WorkspaceSlotReleasePreparation>> ListPreparedAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT preparation_id, slot_id, session_id, generation, state
              FROM workspace_slot_release_preparations
             WHERE state = 'Prepared'
             ORDER BY rowid
             LIMIT 10001
            """;
        List<WorkspaceSlotReleasePreparation> result = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (result.Count >= 10000)
            {
                throw new InvalidOperationException(
                    "The release preparation journal exceeds the safe inventory limit."
                );
            }

            result.Add(ReadPreparation(reader));
        }

        return Task.FromResult<IReadOnlyList<WorkspaceSlotReleasePreparation>>(result);
    }

    private static WorkspaceSlotReleasePreparation ReadPreparation(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            Enum.Parse<WorkspaceSlotReleasePreparationState>(
                reader.GetString(4),
                ignoreCase: false
            )
        );

    private static WorkspaceSlot ReadSlot(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        Enum.Parse<WorkspaceSlotState>(reader.GetString(5), ignoreCase: false),
        reader.IsDBNull(6) ? null : reader.GetString(6)
    );

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand config = connection.CreateCommand();
        config.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        config.ExecuteNonQuery();
        return connection;
    }
}
