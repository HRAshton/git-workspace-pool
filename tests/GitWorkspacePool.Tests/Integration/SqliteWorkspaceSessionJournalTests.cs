using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Durable ownership, history, and cross-process synchronization contracts.
/// </summary>
public sealed class SqliteWorkspaceSessionJournalTests
{
    [Fact]
    public async Task Journal_ReservationAndHistorySurviveReopeningDatabase()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal first = fixture.CreateJournal();
        WorkspaceSession created = await fixture.ReserveAsync(first);

        await first.PinCommitAsync(created.OperationId, new string('a', 40), CancellationToken.None);
        await first.ActivateAsync(created.OperationId, CancellationToken.None);

        SqliteWorkspaceSessionJournal reopened = fixture.CreateJournal();
        WorkspaceSession restored = await fixture.ReserveAsync(reopened);

        Assert.Equal(created.OperationId, restored.OperationId);
        Assert.Equal(WorkspaceSessionState.Active, restored.State);
        Assert.Equal(new string('a', 40), restored.PinnedCommitSha);

        using SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(kind, ',') FROM (
                SELECT kind FROM session_events ORDER BY id
            )
            """;

        Assert.Equal("Reserved,Pinned,Activated", command.ExecuteScalar());
    }

    [Fact]
    public async Task Journal_RejectsDifferentOwnerForSameWorkspacePath()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        _ = await fixture.ReserveAsync(journal);

        await Assert.ThrowsAsync<SqliteException>(
            () =>
                journal.GetOrCreateAsync(
                    "github:other/repo",
                    19,
                    fixture.SourcePath,
                    fixture.WorkspacePath,
                    CancellationToken.None
                )
        );
    }

    [Fact]
    public async Task Journal_RefusesSilentMoveOfExistingSession()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        _ = await fixture.ReserveAsync(journal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                journal.GetOrCreateAsync(
                    "github:owner/repo",
                    17,
                    fixture.SourcePath,
                    fixture.WorkspacePath + "-moved",
                    CancellationToken.None
                )
        );
    }

    [Fact]
    public async Task Journal_PinnedCommitCannotBeReplaced()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        WorkspaceSession session = await fixture.ReserveAsync(journal);

        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                journal.PinCommitAsync(
                    session.OperationId,
                    new string('b', 40),
                    CancellationToken.None
                )
        );

        WorkspaceSession restored = await fixture.ReserveAsync(journal);
        Assert.Equal(new string('a', 40), restored.PinnedCommitSha);
    }

    [Fact]
    public async Task RestoreJournal_IntentionSurvivesRestartAndCompletesOnlyOnce()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        WorkspaceSession owner = await fixture.ReserveAsync(journal);
        await journal.PinCommitAsync(owner.OperationId, new string('a', 40), CancellationToken.None);
        await journal.ActivateAsync(owner.OperationId, CancellationToken.None);
        WorkspaceSnapshotReceipt receipt = new(
            Guid.NewGuid(),
            owner.OperationId,
            Path.Combine(fixture.LockRoot, "snapshot"),
            1,
            1
        );
        await journal.RecordSnapshotAsync(receipt, CancellationToken.None);

        Guid restore = Guid.NewGuid();
        string target = Path.Combine(fixture.LockRoot, "restored");
        await journal.ReserveRestoreAsync(restore, receipt.SnapshotId, target, CancellationToken.None);

        SqliteWorkspaceSessionJournal reopened = fixture.CreateJournal();
        WorkspaceRestoreOperation pending = Assert.Single(
            await reopened.ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(restore, pending.RestoreId);
        Assert.Equal(WorkspaceRestoreState.Preparing, pending.State);
        Assert.Equal(target, pending.Path);

        await reopened.VerifyRestoreAsync(restore, CancellationToken.None);
        Assert.Equal(
            WorkspaceRestoreState.Verified,
            Assert.Single(
                await fixture.CreateJournal().ListRecoveryRestoresAsync(CancellationToken.None)
            ).State
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.VerifyRestoreAsync(restore, CancellationToken.None)
        );
    }

    [Fact]
    public async Task RestoreJournal_RejectsUnknownSnapshot()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        await Assert.ThrowsAsync<SqliteException>(
            () => journal.ReserveRestoreAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                fixture.WorkspacePath,
                CancellationToken.None
            )
        );
        Assert.Empty(await journal.ListRecoveryRestoresAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Journal_SuspendedOwnershipPersistsAcrossDatabaseRestart()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);
        await journal.ActivateAsync(session.OperationId, CancellationToken.None);
        await journal.SuspendAsync(session.OperationId, CancellationToken.None);

        SqliteWorkspaceSessionJournal reopened = fixture.CreateJournal();
        WorkspaceSession? suspended = await reopened.FindAsync(
            session.OperationId,
            CancellationToken.None
        );
        Assert.NotNull(suspended);
        Assert.Equal(WorkspaceSessionState.Suspended, suspended.State);
        Assert.Empty(await reopened.ListActiveAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.SuspendAsync(session.OperationId, CancellationToken.None)
        );
        await reopened.ResumeAsync(session.OperationId, CancellationToken.None);
        Assert.Equal(
            WorkspaceSessionState.Active,
            Assert.Single(await reopened.ListActiveAsync(CancellationToken.None)).State
        );
    }

    [Fact]
    public async Task Journal_RecoveryRequiredCannotBeAutomaticallyActivated()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);
        await journal.RequireRecoveryAsync(session.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.ActivateAsync(session.OperationId, CancellationToken.None)
        );
        Assert.Equal(
            WorkspaceSessionState.RecoveryRequired,
            (await journal.FindAsync(session.OperationId, CancellationToken.None))!.State
        );
    }

    [Fact]
    public async Task SchemaV3Upgrade_PreservesSessionsSnapshotsAndRestoreReferencesInV5()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.DatabasePath)!);
        Guid sessionId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        Guid restoreId = Guid.NewGuid();

        // Simulates a real pre-v4 state database with the old CHECK constraint
        // and two levels of foreign keys referencing the session record.
        using (SqliteConnection database = new(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        ))
        {
            database.Open();
            using SqliteCommand seed = database.CreateCommand();
            seed.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE sessions (
                    operation_id TEXT PRIMARY KEY,
                    repository_key TEXT NOT NULL COLLATE NOCASE,
                    pr_number INTEGER NOT NULL CHECK (pr_number > 0),
                    source_path TEXT NOT NULL COLLATE NOCASE,
                    workspace_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    state TEXT NOT NULL CHECK (
                        state IN ('Preparing', 'Active', 'RecoveryRequired')
                    ),
                    pinned_commit TEXT,
                    updated_utc TEXT NOT NULL,
                    UNIQUE (repository_key, pr_number)
                );
                CREATE TABLE session_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    operation_id TEXT NOT NULL REFERENCES sessions(operation_id),
                    kind TEXT NOT NULL,
                    created_utc TEXT NOT NULL
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
                    target_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    state TEXT NOT NULL CHECK (state IN ('Preparing', 'Verified')),
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO sessions (
                    operation_id, repository_key, pr_number, source_path,
                    workspace_path, state, pinned_commit, updated_utc
                ) VALUES (
                    $session, 'github:owner/repo', 17, $source, $workspace,
                    'Active', $sha, $utc
                );
                INSERT INTO snapshots (
                    snapshot_id, operation_id, folder, file_count, copied_bytes, created_utc
                ) VALUES ($snapshot, $session, $folder, 1, 42, $utc);
                INSERT INTO restore_operations (
                    restore_id, snapshot_id, target_path, state, created_utc, updated_utc
                ) VALUES ($restore, $snapshot, $target, 'Preparing', $utc, $utc);
                INSERT INTO session_events (operation_id, kind, created_utc)
                VALUES ($session, 'Activated', $utc);
                PRAGMA user_version = 3;
                """;
            seed.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            seed.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
            seed.Parameters.AddWithValue("$restore", restoreId.ToString("D"));
            seed.Parameters.AddWithValue("$source", fixture.SourcePath);
            seed.Parameters.AddWithValue("$workspace", fixture.WorkspacePath);
            seed.Parameters.AddWithValue("$sha", new string('a', 40));
            seed.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            seed.Parameters.AddWithValue(
                "$folder",
                Path.Combine(fixture.LockRoot, "snapshot")
            );
            seed.Parameters.AddWithValue(
                "$target",
                Path.Combine(fixture.LockRoot, "recovered")
            );
            seed.ExecuteNonQuery();
        }

        SqliteWorkspaceSessionJournal upgraded = fixture.CreateJournal();
        WorkspaceSession? session = await upgraded.FindAsync(sessionId, CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(WorkspaceSessionState.Active, session.State);
        Assert.Equal(snapshotId, Assert.Single(
            await upgraded.ListSnapshotsAsync(CancellationToken.None)
        ).Receipt.SnapshotId);
        Assert.Equal(restoreId, Assert.Single(
            await upgraded.ListRecoveryRestoresAsync(CancellationToken.None)
        ).RestoreId);

        await upgraded.SuspendAsync(sessionId, CancellationToken.None);
        Assert.Equal(
            WorkspaceSessionState.Suspended,
            (await upgraded.FindAsync(sessionId, CancellationToken.None))!.State
        );
        await upgraded.ResumeAsync(sessionId, CancellationToken.None);

        using SqliteConnection verified = new(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        );
        verified.Open();
        using SqliteCommand check = verified.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(5L, check.ExecuteScalar());
    }

    [Fact]
    public async Task Journal_ParkingIntentsSurviveRestartAndRejectIllegalTransitions()
    {
        using Fixture fixture = new();
        SqliteWorkspaceSessionJournal journal = fixture.CreateJournal();
        WorkspaceSession session = await fixture.ReserveAsync(journal);
        await journal.PinCommitAsync(session.OperationId, new string('a', 40), CancellationToken.None);
        await journal.ActivateAsync(session.OperationId, CancellationToken.None);
        string destination = Path.Combine(fixture.LockRoot, "parked");

        await journal.BeginParkingAsync(
            session.OperationId,
            destination,
            new string('b', 40),
            CancellationToken.None
        );

        SqliteWorkspaceSessionJournal reopened = fixture.CreateJournal();
        WorkspaceSession preparing = Assert.Single(
            await reopened.ListParkCandidatesAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceSessionState.Parking, preparing.State);
        Assert.Equal(destination, preparing.ParkingPath);
        Assert.Equal(new string('b', 40), preparing.ParkingCommitSha);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.ResumeAsync(session.OperationId, CancellationToken.None)
        );
        await reopened.CompleteParkingAsync(session.OperationId, CancellationToken.None);
        Assert.Equal(WorkspaceSessionState.Parked,
            Assert.Single(await reopened.ListUnparkCandidatesAsync(CancellationToken.None)).State);

        await reopened.BeginUnparkingAsync(session.OperationId, CancellationToken.None);
        Assert.Equal(WorkspaceSessionState.Unparking,
            (await reopened.FindAsync(session.OperationId, CancellationToken.None))!.State);
        await reopened.CompleteUnparkingAsync(session.OperationId, CancellationToken.None);

        WorkspaceSession resumed = (await reopened.FindAsync(
            session.OperationId, CancellationToken.None
        ))!;
        Assert.Equal(WorkspaceSessionState.Active, resumed.State);
        Assert.Null(resumed.ParkingPath);
        Assert.Null(resumed.ParkingCommitSha);
    }

    [Fact]
    public async Task FileOperationLock_SerializesIndependentInstances()
    {
        using Fixture fixture = new();
        FileWorkspaceOperationLock first = new(fixture.LockRoot);
        FileWorkspaceOperationLock second = new(fixture.LockRoot);

        IAsyncDisposable owner = await first.AcquireAsync(
            fixture.WorkspacePath,
            CancellationToken.None
        );

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<IAsyncDisposable> waiter = second.AcquireAsync(fixture.WorkspacePath, timeout.Token);
        try
        {
            await Task.Delay(100);
            Assert.False(waiter.IsCompleted);
        }
        finally
        {
            await owner.DisposeAsync();
        }

        await using IAsyncDisposable acquired = await waiter;
        Assert.NotNull(acquired);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "gwp-journal-" + Guid.NewGuid().ToString("N")
        );

        public string DatabasePath => Path.Combine(_root, "state", "sessions.db");

        public string SourcePath => Path.Combine(_root, "clone");

        public string WorkspacePath => Path.Combine(_root, "workspaces", "pr-17");

        public string LockRoot => Path.Combine(_root, "locks");

        public SqliteWorkspaceSessionJournal CreateJournal() => new(DatabasePath);

        public Task<WorkspaceSession> ReserveAsync(SqliteWorkspaceSessionJournal journal) =>
            journal.GetOrCreateAsync(
                "github:owner/repo",
                17,
                SourcePath,
                WorkspacePath,
                CancellationToken.None
            );

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
