using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain.WarmPool;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Transactional slot compare-and-swap. Slot readiness must be established by a
/// separate, not-yet-enabled verified release workflow.
/// </summary>
public sealed class SqliteWorkspaceSlotRegistry : IWorkspaceSlotRegistry, IWorkspaceSlotOwnershipAuditor, IWorkspaceSlotReleasePreparationJournal
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
            SELECT preparation_id, slot_id, session_id, generation, state
              FROM workspace_slot_release_preparations
             WHERE session_id = $session AND state = 'Prepared'
            """;
        existing.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                WorkspaceSlotReleasePreparation saved = ReadPreparation(reader);
                return Task.FromResult<WorkspaceSlotReleasePreparation?>(
                    saved.SlotId == slotId && saved.Generation == expectedGeneration
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
