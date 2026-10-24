using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Additive branch ownership table in the existing backed-up SQLite journal; no PR migration is needed.</summary>
public sealed class SqliteGitBranchSessionStore : IGitBranchSessionStore
{
    private readonly string _connectionString;

    /// <summary>Initializes a journal without rewriting existing PR sessions or schema version.</summary>
    public SqliteGitBranchSessionStore(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS git_branch_sessions (
                operation_id TEXT PRIMARY KEY,
                clone_url TEXT NOT NULL COLLATE BINARY,
                branch TEXT NOT NULL COLLATE BINARY,
                source_path TEXT NOT NULL,
                workspace_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                state TEXT NOT NULL CHECK (state IN ('Preparing', 'Active', 'RecoveryRequired')),
                pinned_commit TEXT,
                UNIQUE (clone_url, branch)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public Task<GitBranchSession?> FindAsync(GitBranchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        return Task.FromResult(Find(connection, null, request));
    }

    /// <inheritdoc/>
    public Task<GitBranchSession> ReserveAsync(GitBranchRequest request, GitRepositoryRegistration repository,
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsValid || !GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl))
            throw new ArgumentException("Invalid branch reservation identity.");
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        GitBranchSession? existing = Find(connection, transaction, request);
        if (existing is not null)
        {
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!existing.SourcePath.Equals(Path.GetFullPath(repository.LocalPath), comparison)
                || !existing.WorkspacePath.Equals(Path.GetFullPath(path), comparison))
                throw new InvalidOperationException("This branch already owns a workspace at different configured paths.");
            transaction.Commit();
            return Task.FromResult(existing);
        }
        GitBranchSession created = new(Guid.NewGuid(), GitBranchRequest.NormalizeCloneUrl(request.CloneUrl),
            request.Branch, Path.GetFullPath(repository.LocalPath), Path.GetFullPath(path),
            WorkspaceSessionState.Preparing, null);
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO git_branch_sessions(operation_id, clone_url, branch, source_path, workspace_path, state)
            VALUES ($id, $url, $branch, $source, $path, 'Preparing');
            """;
        insert.Parameters.AddWithValue("$id", created.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$url", created.CloneUrl);
        insert.Parameters.AddWithValue("$branch", created.Branch);
        insert.Parameters.AddWithValue("$source", created.SourcePath);
        insert.Parameters.AddWithValue("$path", created.WorkspacePath);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return Task.FromResult(created);
    }

    /// <inheritdoc/>
    public Task PinAsync(Guid operationId, string commit, CancellationToken cancellationToken)
    {
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
            throw new ArgumentException("A full Git commit ID is required.");
        return TransitionAsync(operationId, """
            UPDATE git_branch_sessions SET pinned_commit = $commit
            WHERE operation_id = $id AND state = 'Preparing'
              AND (pinned_commit IS NULL OR pinned_commit = $commit COLLATE NOCASE);
            """, commit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task ActivateAsync(Guid operationId, CancellationToken cancellationToken) =>
        TransitionAsync(operationId, """
            UPDATE git_branch_sessions SET state = 'Active'
            WHERE operation_id = $id AND state = 'Preparing' AND pinned_commit IS NOT NULL;
            """, null, cancellationToken);

    /// <inheritdoc/>
    public Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken) =>
        TransitionAsync(operationId, """
            UPDATE git_branch_sessions SET state = 'RecoveryRequired' WHERE operation_id = $id;
            """, null, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<GitBranchSession>> ListUnfinishedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, clone_url, branch, source_path, workspace_path, state, pinned_commit
            FROM git_branch_sessions WHERE state != 'Active' ORDER BY clone_url, branch LIMIT 101;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<GitBranchSession> sessions = [];
        while (reader.Read()) sessions.Add(Map(reader));
        return Task.FromResult<IReadOnlyList<GitBranchSession>>(sessions);
    }

    private static GitBranchSession? Find(SqliteConnection connection, SqliteTransaction? transaction,
        GitBranchRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, clone_url, branch, source_path, workspace_path, state, pinned_commit
            FROM git_branch_sessions WHERE clone_url = $url AND branch = $branch;
            """;
        command.Parameters.AddWithValue("$url", GitBranchRequest.NormalizeCloneUrl(request.CloneUrl));
        command.Parameters.AddWithValue("$branch", request.Branch);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static GitBranchSession Map(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), Enum.Parse<WorkspaceSessionState>(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    private Task TransitionAsync(Guid operationId, string sql, string? commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        if (commit is not null) command.Parameters.AddWithValue("$commit", commit);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Branch ownership transition was rejected; evidence remains retained.");
        return Task.CompletedTask;
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
