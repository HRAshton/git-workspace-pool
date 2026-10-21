using System.Globalization;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Durable, transactional workspace ownership and append-only operation events.
/// </summary>
public sealed class SqliteWorkspaceSessionJournal : IWorkspaceSessionJournal, IWorkspaceSnapshotSessionStore
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes or upgrades the local SQLite journal to schema version two.
    /// </summary>
    public SqliteWorkspaceSessionJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        };
        _connectionString = builder.ToString();

        using SqliteConnection connection = Open();
        using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        long currentVersion = (long)version.ExecuteScalar()!;

        if (currentVersion > 2)
        {
            throw new InvalidOperationException("Session journal schema is newer than this application.");
        }

        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                operation_id TEXT PRIMARY KEY,
                repository_key TEXT NOT NULL COLLATE NOCASE,
                pr_number INTEGER NOT NULL CHECK (pr_number > 0),
                source_path TEXT NOT NULL COLLATE NOCASE,
                workspace_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                state TEXT NOT NULL CHECK (state IN ('Preparing', 'Active', 'RecoveryRequired')),
                pinned_commit TEXT,
                updated_utc TEXT NOT NULL,
                UNIQUE (repository_key, pr_number)
            );

            CREATE TABLE IF NOT EXISTS session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL REFERENCES sessions(operation_id),
                kind TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );


            CREATE TABLE IF NOT EXISTS snapshots (
                snapshot_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES sessions(operation_id),
                folder TEXT NOT NULL UNIQUE,
                file_count INTEGER NOT NULL,
                copied_bytes INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );

            PRAGMA user_version = 2;
            """;
        schema.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public Task<WorkspaceSession> GetOrCreateAsync(
        string repositoryKey,
        int pullRequestNumber,
        string sourcePath,
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestNumber));
        }

        sourcePath = Path.GetFullPath(sourcePath);
        workspacePath = Path.GetFullPath(workspacePath);

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand get = connection.CreateCommand();
        get.Transaction = transaction;
        get.CommandText = """
            SELECT operation_id, repository_key, pr_number, source_path,
                   workspace_path, state, pinned_commit
              FROM sessions
             WHERE repository_key = $repository_key AND pr_number = $pr_number
            """;
        get.Parameters.AddWithValue("$repository_key", repositoryKey);
        get.Parameters.AddWithValue("$pr_number", pullRequestNumber);

        WorkspaceSession? existing;

        using (SqliteDataReader reader = get.ExecuteReader())
        {
            existing = reader.Read() ? Map(reader) : null;
        }

        if (existing is not null)
        {
            if (
                !existing.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                || !existing.WorkspacePath.Equals(workspacePath, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new InvalidOperationException(
                    "The PR is already owned at different paths; automatic reassignment is forbidden."
                );
            }

            transaction.Commit();
            return Task.FromResult(existing);
        }

        WorkspaceSession session = new(
            Guid.NewGuid(),
            repositoryKey,
            pullRequestNumber,
            sourcePath,
            workspacePath,
            WorkspaceSessionState.Preparing,
            null
        );

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO sessions(
                operation_id, repository_key, pr_number, source_path, workspace_path,
                state, pinned_commit, updated_utc
            ) VALUES ($id, $key, $pr, $source, $workspace, 'Preparing', NULL, $utc)
            """;
        insert.Parameters.AddWithValue("$id", session.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$key", repositoryKey);
        insert.Parameters.AddWithValue("$pr", pullRequestNumber);
        insert.Parameters.AddWithValue("$source", sourcePath);
        insert.Parameters.AddWithValue("$workspace", workspacePath);
        insert.Parameters.AddWithValue("$utc", UtcNow());
        insert.ExecuteNonQuery();

        AddEvent(connection, transaction, session.OperationId, "Reserved");
        transaction.Commit();
        return Task.FromResult(session);
    }

    /// <inheritdoc/>
    public Task<WorkspaceSession?> FindAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, repository_key, pr_number, source_path,
                   workspace_path, state, pinned_commit
              FROM sessions
             WHERE operation_id = $id
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));

        using SqliteDataReader reader = command.ExecuteReader();
        return Task.FromResult(reader.Read() ? Map(reader) : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WorkspaceSession>> ListActiveAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, repository_key, pr_number, source_path,
                   workspace_path, state, pinned_commit
              FROM sessions
             WHERE state = 'Active'
             ORDER BY updated_utc DESC
            """;

        List<WorkspaceSession> sessions = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(Map(reader));
        }

        return Task.FromResult<IReadOnlyList<WorkspaceSession>>(sessions);
    }

    /// <inheritdoc/>
    public Task RecordSnapshotAsync(
        WorkspaceSnapshotReceipt receipt,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots(
                snapshot_id, operation_id, folder, file_count, copied_bytes, created_utc
            )
            SELECT $snapshot, operation_id, $folder, $files, $bytes, $utc
              FROM sessions
             WHERE operation_id = $owner AND state = 'Active'
            """;
        command.Parameters.AddWithValue("$snapshot", receipt.SnapshotId.ToString("D"));
        command.Parameters.AddWithValue("$owner", receipt.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$folder", Path.GetFullPath(receipt.Folder));
        command.Parameters.AddWithValue("$files", receipt.FileCount);
        command.Parameters.AddWithValue("$bytes", receipt.CopiedBytes);
        command.Parameters.AddWithValue("$utc", UtcNow());

        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "Cannot register a snapshot of an unknown or inactive session."
            );
        }

        AddEvent(connection, transaction, receipt.OperationId, "SnapshotCaptured");
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PinCommitAsync(
        Guid operationId,
        string commitSha,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (
            commitSha.Length is not (40 or 64)
            || commitSha.Any(character => !Uri.IsHexDigit(character))
        )
        {
            throw new ArgumentException("Expected a full Git commit object ID.", nameof(commitSha));
        }

        Transition(
            operationId,
            "Pinned",
            "UPDATE sessions SET pinned_commit = $commit, updated_utc = $utc "
                + "WHERE operation_id = $id AND state = 'Preparing' "
                + "AND (pinned_commit IS NULL OR pinned_commit = $commit)",
            command => command.Parameters.AddWithValue("$commit", commitSha.ToLowerInvariant()),
            cancellationToken
        );
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ActivateAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Transition(
            operationId,
            "Activated",
            "UPDATE sessions SET state = 'Active', updated_utc = $utc "
                + "WHERE operation_id = $id AND state IN ('Preparing', 'RecoveryRequired') "
                + "AND pinned_commit IS NOT NULL",
            null,
            cancellationToken
        );
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Transition(
            operationId,
            "RecoveryRequired",
            "UPDATE sessions SET state = 'RecoveryRequired', updated_utc = $utc "
                + "WHERE operation_id = $id AND state IN ('Preparing', 'Active', 'RecoveryRequired')",
            null,
            cancellationToken
        );
        return Task.CompletedTask;
    }

    private static WorkspaceSession Map(SqliteDataReader reader)
    {
        return new WorkspaceSession(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            Enum.Parse<WorkspaceSessionState>(reader.GetString(5), ignoreCase: false),
            reader.IsDBNull(6) ? null : reader.GetString(6)
        );
    }

    private static string UtcNow() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void AddEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        string kind
    )
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_events(operation_id, kind, created_utc)
            VALUES ($id, $kind, $utc)
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$utc", UtcNow());
        command.ExecuteNonQuery();
    }

    private void Transition(
        Guid operationId,
        string eventKind,
        string sql,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$utc", UtcNow());
        configure?.Invoke(command);

        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The journal transition was rejected; session ownership was not changed."
            );
        }

        AddEvent(connection, transaction, operationId, eventKind);
        transaction.Commit();
    }
}
