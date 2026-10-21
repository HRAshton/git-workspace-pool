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
