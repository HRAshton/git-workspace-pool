using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain.WarmPool;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Transactional slot compare-and-swap. Slot readiness must be established by a
/// separate, not-yet-enabled verified release workflow.
/// </summary>
public sealed class SqliteWorkspaceSlotRegistry : IWorkspaceSlotRegistry
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
