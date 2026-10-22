using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Durable slot generations and lease uniqueness; no Git mutation or checkout.
/// </summary>
public sealed class SqliteWorkspaceSlotRegistryTests
{
    [Fact]
    public async Task SessionReservation_ProtectsSlotWithExactlyOneHeldLease()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);

        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await registry.FindHeldSlotAsync(session.OperationId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, slot.State);
        Assert.Equal(0, slot.Generation);
        Assert.Equal(session.WorkspacePath, slot.WorkspacePath);
        Assert.Equal(session.RepositoryKey, slot.RepositoryKey);

        Assert.Null(
            await registry.TryLeaseReadyAsync(
                slot.SlotId, session.OperationId, 0, CancellationToken.None
            )
        );

        // Reopening the database cannot duplicate the session or the protective lease.
        _ = new SqliteWorkspaceSessionJournal(fixture.DatabasePath);
        _ = await fixture.ReserveAsync(new SqliteWorkspaceSessionJournal(fixture.DatabasePath));

        using SqliteConnection db = fixture.Open();
        using SqliteCommand check = db.CreateCommand();
        check.CommandText = "SELECT count(*) FROM workspace_slots";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT count(*) FROM workspace_slot_leases WHERE state = 'Held'";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    [Fact]
    public async Task MigrationFromV5_ProtectsParkedWorktreeAndKeepsRecoveryReferences()
    {
        using Fixture fixture = new();
        Guid sessionId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        Guid restoreId = Guid.NewGuid();
        string parked = Path.Combine(fixture.Root, "parking", sessionId.ToString("N"));

        using (SqliteConnection db = fixture.Open())
        {
            using SqliteCommand seed = db.CreateCommand();
            seed.CommandText = """
                CREATE TABLE sessions (
                    operation_id TEXT PRIMARY KEY,
                    repository_key TEXT NOT NULL COLLATE NOCASE,
                    pr_number INTEGER NOT NULL,
                    source_path TEXT NOT NULL COLLATE NOCASE,
                    workspace_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    state TEXT NOT NULL,
                    pinned_commit TEXT,
                    parking_path TEXT COLLATE NOCASE UNIQUE,
                    parking_head TEXT,
                    updated_utc TEXT NOT NULL,
                    UNIQUE (repository_key, pr_number)
                );
                CREATE TABLE snapshots (
                    snapshot_id TEXT PRIMARY KEY,
                    operation_id TEXT NOT NULL REFERENCES sessions(operation_id),
                    folder TEXT NOT NULL UNIQUE,
                    file_count INTEGER NOT NULL,
                    copied_bytes INTEGER NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE restore_operations (
                    restore_id TEXT PRIMARY KEY,
                    snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                    target_path TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                INSERT INTO sessions (
                    operation_id, repository_key, pr_number, source_path,
                    workspace_path, state, pinned_commit, parking_path, parking_head,
                    updated_utc
                ) VALUES (
                    $session, 'github:owner/repo', 17, $source, $path,
                    'Parked', $head, $parked, $head, $utc
                );
                INSERT INTO snapshots (
                    snapshot_id, operation_id, folder, file_count, copied_bytes, created_utc
                ) VALUES ($snapshot, $session, $folder, 0, 0, $utc);
                INSERT INTO restore_operations (
                    restore_id, snapshot_id, target_path, state, created_utc, updated_utc
                ) VALUES ($restore, $snapshot, $target, 'Verified', $utc, $utc);
                PRAGMA user_version = 5;
                """;
            seed.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            seed.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
            seed.Parameters.AddWithValue("$restore", restoreId.ToString("D"));
            seed.Parameters.AddWithValue("$source", fixture.SourcePath);
            seed.Parameters.AddWithValue("$path", fixture.WorkspacePath);
            seed.Parameters.AddWithValue("$parked", parked);
            seed.Parameters.AddWithValue("$head", new string('a', 40));
            seed.Parameters.AddWithValue("$folder", Path.Combine(fixture.Root, "snapshot"));
            seed.Parameters.AddWithValue("$target", Path.Combine(fixture.Root, "recovered"));
            seed.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }

        SqliteWorkspaceSessionJournal upgraded = new(fixture.DatabasePath);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceSlot protectedSlot = Assert.IsType<WorkspaceSlot>(
            await registry.FindHeldSlotAsync(sessionId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, protectedSlot.State);
        Assert.Equal(0, protectedSlot.Generation);
        Assert.Equal(fixture.WorkspacePath, protectedSlot.WorkspacePath);

        WorkspaceSession session = Assert.IsType<WorkspaceSession>(
            await upgraded.FindAsync(sessionId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSessionState.Parked, session.State);
        Assert.Equal(parked, session.ParkingPath);
        Assert.Equal(snapshotId, Assert.Single(
            await upgraded.ListSnapshotsAsync(CancellationToken.None)
        ).Receipt.SnapshotId);
        Assert.Equal(restoreId, Assert.Single(
            await upgraded.ListRecoveryRestoresAsync(CancellationToken.None)
        ).RestoreId);

        using SqliteConnection verified = fixture.Open();
        using SqliteCommand check = verified.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(7L, check.ExecuteScalar());
        check.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public async Task SlotAudit_IsEmptyForNewlyProtectedSession()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        _ = await fixture.ReserveAsync(journal);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);

        Assert.Empty(await registry.AuditAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SlotAudit_DetectsReadySlotWithHeldLease()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);

        using SqliteConnection connection = fixture.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_slots SET state = 'Ready'
             WHERE workspace_path = $path
            """;
        command.Parameters.AddWithValue("$path", session.WorkspacePath);
        Assert.Equal(1, command.ExecuteNonQuery());

        WorkspaceSlotIntegrityFinding issue = Assert.Single(
            await registry.AuditAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotIntegrityIssue.ReadyWithHeldLease, issue.Issue);
        Assert.Equal(session.OperationId, issue.SessionId);
    }

    [Fact]
    public async Task SlotAudit_DetectsMissingLeaseAndGenerationMismatch()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        _ = await fixture.ReserveAsync(journal);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);

        using SqliteConnection connection = fixture.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_slot_leases
               SET generation = generation + 1
             WHERE state = 'Held'
            """;
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Contains(
            await registry.AuditAsync(CancellationToken.None),
            issue => issue.Issue == WorkspaceSlotIntegrityIssue.LeaseGenerationMismatch
        );

        command.CommandText = "DELETE FROM workspace_slot_leases WHERE state = 'Held'";
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal(
            WorkspaceSlotIntegrityIssue.ProtectedLeaseMissing,
            Assert.Single(await registry.AuditAsync(CancellationToken.None)).Issue
        );
    }

    [Fact]
    public async Task ReleasePreparation_RequiresProtectedParkedOwnerAndExpectedGeneration()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceSlot protectedSlot = Assert.IsType<WorkspaceSlot>(
            await registry.FindHeldSlotAsync(session.OperationId, CancellationToken.None)
        );

        Assert.Null(await registry.TryPrepareAsync(
            session.OperationId, protectedSlot.SlotId, 0, CancellationToken.None
        ));

        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);
        await journal.ActivateAsync(session.OperationId, CancellationToken.None);
        string destination = Path.Combine(fixture.Root, "parked");
        await journal.BeginParkingAsync(
            session.OperationId, destination, new string('a', 40), CancellationToken.None
        );
        await journal.CompleteParkingAsync(session.OperationId, CancellationToken.None);

        Assert.Null(await registry.TryPrepareAsync(
            session.OperationId, protectedSlot.SlotId, 1, CancellationToken.None
        ));

        WorkspaceSlotReleasePreparation prepared = Assert.IsType<WorkspaceSlotReleasePreparation>(
            await registry.TryPrepareAsync(
                session.OperationId, protectedSlot.SlotId, 0, CancellationToken.None
            )
        );
        WorkspaceSlotReleasePreparation retry = Assert.IsType<WorkspaceSlotReleasePreparation>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath).TryPrepareAsync(
                session.OperationId, protectedSlot.SlotId, 0, CancellationToken.None
            )
        );
        Assert.Equal(prepared, retry);
        Assert.Equal(
            [prepared],
            await registry.ListPreparedAsync(CancellationToken.None)
        );

        WorkspaceSlot held = Assert.IsType<WorkspaceSlot>(
            await registry.FindHeldSlotAsync(session.OperationId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, held.State);
        Assert.Equal(0, held.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.BeginUnparkingAsync(session.OperationId, CancellationToken.None)
        );

        Assert.True(await registry.CancelAsync(prepared.PreparationId, CancellationToken.None));
        Assert.False(await registry.CancelAsync(prepared.PreparationId, CancellationToken.None));
        Assert.Empty(await registry.ListPreparedAsync(CancellationToken.None));
        await journal.BeginUnparkingAsync(session.OperationId, CancellationToken.None);
    }

    [Fact]
    public async Task ReleasePreparation_RacingConnectionsReturnOneDurableIntent()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);
        await journal.ActivateAsync(session.OperationId, CancellationToken.None);
        await journal.BeginParkingAsync(
            session.OperationId,
            Path.Combine(fixture.Root, "parked"),
            new string('a', 40),
            CancellationToken.None
        );
        await journal.CompleteParkingAsync(session.OperationId, CancellationToken.None);

        SqliteWorkspaceSlotRegistry first = new(fixture.DatabasePath);
        SqliteWorkspaceSlotRegistry second = new(fixture.DatabasePath);
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await first.FindHeldSlotAsync(session.OperationId, CancellationToken.None)
        );

        Task<WorkspaceSlotReleasePreparation?> left = Task.Run(async () =>
            await first.TryPrepareAsync(
                session.OperationId, slot.SlotId, slot.Generation, CancellationToken.None
            )
        );
        Task<WorkspaceSlotReleasePreparation?> right = Task.Run(async () =>
            await second.TryPrepareAsync(
                session.OperationId, slot.SlotId, slot.Generation, CancellationToken.None
            )
        );

        WorkspaceSlotReleasePreparation?[] results = await Task.WhenAll(left, right);
        Assert.Equal(
            results[0]?.PreparationId,
            results[1]?.PreparationId
        );
        Assert.NotNull(results[0]);
        Assert.Single(await first.ListPreparedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadySlotCompareAndSwap_GrantsExactlyOneGenerationLease()
    {
        using Fixture fixture = new();
        (Guid slotId, Guid sessionId) = fixture.SeedSyntheticReadySlot(7);
        SqliteWorkspaceSlotRegistry first = new(fixture.DatabasePath);
        SqliteWorkspaceSlotRegistry second = new(fixture.DatabasePath);

        // Independent connections contend for the same durable generation.
        Task<WorkspaceSlotLease?> left = Task.Run(async () =>
            await first.TryLeaseReadyAsync(slotId, sessionId, 7, CancellationToken.None)
        );
        Task<WorkspaceSlotLease?> right = Task.Run(async () =>
            await second.TryLeaseReadyAsync(slotId, sessionId, 7, CancellationToken.None)
        );

        WorkspaceSlotLease?[] responses = await Task.WhenAll(left, right);
        WorkspaceSlotLease result = Assert.Single(responses.OfType<WorkspaceSlotLease>());
        Assert.Equal(8, result.Generation);
        Assert.Equal(slotId, result.SlotId);
        Assert.Equal(sessionId, result.SessionId);

        WorkspaceSlot observed = Assert.IsType<WorkspaceSlot>(
            await second.FindSlotAsync(slotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Reserved, observed.State);
        Assert.Equal(8, observed.Generation);

        Assert.Null(await first.TryLeaseReadyAsync(slotId, sessionId, 7, CancellationToken.None));

        using SqliteConnection db = fixture.Open();
        using SqliteCommand count = db.CreateCommand();
        count.CommandText = """
            SELECT count(*) FROM workspace_slot_leases
             WHERE state = 'Held' AND slot_id = $slot
            """;
        count.Parameters.AddWithValue("$slot", slotId.ToString("N"));
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public async Task ReadySlotCompareAndSwap_RejectsStaleOrUntrustedOwner()
    {
        using Fixture fixture = new();
        (Guid slotId, Guid sessionId) = fixture.SeedSyntheticReadySlot(3);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);

        Assert.Null(await registry.TryLeaseReadyAsync(
            slotId, sessionId, 2, CancellationToken.None
        ));
        Assert.Null(await registry.TryLeaseReadyAsync(
            slotId, Guid.NewGuid(), 3, CancellationToken.None
        ));

        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await registry.FindSlotAsync(slotId, CancellationToken.None)
        );
        Assert.Equal(3, slot.Generation);
        Assert.Equal(WorkspaceSlotState.Ready, slot.State);
        Assert.Null(await registry.FindHeldSlotAsync(sessionId, CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "gwp-slots-" + Guid.NewGuid().ToString("N")
        );

        public string DatabasePath => Path.Combine(Root, "state", "sessions.db");

        public string SourcePath => Path.Combine(Root, "source");

        public string WorkspacePath => Path.Combine(Root, "workspaces", "pr-17");

        public SqliteConnection Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            SqliteConnection db = new($"Data Source={DatabasePath};Pooling=False");
            db.Open();
            return db;
        }

        public Task<WorkspaceSession> ReserveAsync(SqliteWorkspaceSessionJournal journal) =>
            journal.GetOrCreateAsync(
                "github:owner/repo",
                17,
                SourcePath,
                WorkspacePath,
                CancellationToken.None
            );

        /// <summary>
        /// Synthetic verified Ready state for CAS tests only. Production code
        /// intentionally cannot mark a slot Ready until safe release exists.
        /// </summary>
        public (Guid SlotId, Guid SessionId) SeedSyntheticReadySlot(long generation)
        {
            _ = new SqliteWorkspaceSessionJournal(DatabasePath);
            Guid sessionId = Guid.NewGuid();
            Guid slotId = Guid.NewGuid();

            using SqliteConnection db = Open();
            using SqliteCommand insert = db.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions (
                    operation_id, repository_key, pr_number, source_path,
                    workspace_path, state, pinned_commit, updated_utc
                ) VALUES (
                    $session, 'github:owner/repo', 17, $source, $path,
                    'Preparing', NULL, $utc
                );
                INSERT INTO workspace_slots (
                    slot_id, repository_key, source_path, workspace_path,
                    generation, state, updated_utc
                ) VALUES (
                    $slot, 'github:owner/repo', $source, $path, $generation,
                    'Ready', $utc
                );
                """;
            insert.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            insert.Parameters.AddWithValue("$slot", slotId.ToString("N"));
            insert.Parameters.AddWithValue("$source", SourcePath);
            insert.Parameters.AddWithValue("$path", WorkspacePath);
            insert.Parameters.AddWithValue("$generation", generation);
            insert.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();

            return (slotId, sessionId);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
