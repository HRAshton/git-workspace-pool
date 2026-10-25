using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Version-one ownership store, intentionally separate from every legacy session database.</summary>
public sealed class SqliteWarmWorkspaceRegistry : IWarmWorkspaceRegistry
{
    private const int ApplicationId = 1196904497;
    private readonly string _connectionString;

    /// <summary>Opens only the new workspaces.db schema. Foreign files are never migrated.</summary>
    public SqliteWarmWorkspaceRegistry(string path)
    {
        path = Path.GetFullPath(path);
        if (Path.GetFileName(path) != "workspaces.db") throw new ArgumentException("Use a distinct warm-v1/workspaces.db path.");
        WorkspacePathSafety.EnsureNoLinksInAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool exists = File.Exists(path);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id";
        long application = (long)command.ExecuteScalar()!;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (application == ApplicationId && version == 1) { transaction.Commit(); return; }
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        if (exists || application != 0 || version != 0 || (long)command.ExecuteScalar()! != 0)
            throw new InvalidOperationException("Foreign or incomplete ownership database. Preserve it for manual inspection; no migration was attempted.");
        command.CommandText = $"""
            CREATE TABLE workspaces (
                workspace_id TEXT PRIMARY KEY NOT NULL,
                repository_key TEXT NOT NULL,
                source_path TEXT NOT NULL,
                absolute_path TEXT NOT NULL COLLATE {(OperatingSystem.IsWindows() ? "NOCASE" : "BINARY")} UNIQUE,
                state TEXT NOT NULL CHECK(state IN ('Available','Assigned','Transitioning','Quarantined')),
                branch TEXT,
                recorded_head TEXT NOT NULL,
                transition_target TEXT,
                artifact_fingerprint TEXT,
                updated_utc TEXT NOT NULL,
                CHECK(state != 'Assigned' OR branch IS NOT NULL),
                CHECK(state != 'Available' OR branch IS NULL),
                CHECK((state = 'Transitioning') = (transition_target IS NOT NULL))
            );
            CREATE UNIQUE INDEX branch_owner ON workspaces(repository_key,branch) WHERE branch IS NOT NULL;
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc/>
    public IReadOnlyList<WarmWorkspace> List(string repositoryKey)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id,repository_key,source_path,absolute_path,state,branch,recorded_head,transition_target,artifact_fingerprint FROM workspaces WHERE repository_key=$repo ORDER BY workspace_id";
        command.Parameters.AddWithValue("$repo", repositoryKey);
        using SqliteDataReader reader = command.ExecuteReader();
        List<WarmWorkspace> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Enum.Parse<WarmWorkspaceState>(reader.GetString(4)), Nullable(reader, 5), reader.GetString(6), Nullable(reader, 7), Nullable(reader, 8)));
        return rows;
    }

    /// <inheritdoc/>
    public void InsertIntent(WarmWorkspace workspace)
    {
        if (workspace.State != WarmWorkspaceState.Transitioning || workspace.Target is null) throw new ArgumentException("Persist intent before checkout.");
        Execute("INSERT INTO workspaces VALUES($id,$repo,$source,$path,'Transitioning',$branch,$head,$target,NULL,$utc)",
            ("$id", workspace.Id), ("$repo", workspace.RepositoryKey), ("$source", Canonical(workspace.SourcePath)),
            ("$path", Canonical(workspace.Path)), ("$branch", workspace.Branch), ("$head", workspace.Head), ("$target", workspace.Target));
    }

    /// <inheritdoc/>
    public void BeginTransition(string id, string branch, string target) => Execute(
        "UPDATE workspaces SET state='Transitioning',branch=$branch,transition_target=$target,updated_utc=$utc WHERE workspace_id=$id AND state='Available'",
        ("$id", id), ("$branch", branch), ("$target", target));

    /// <inheritdoc/>
    public void Complete(string id, WarmWorkspaceState state, string head, string? fingerprint)
    {
        if (state is not (WarmWorkspaceState.Assigned or WarmWorkspaceState.Available)) throw new ArgumentException("Invalid completion state.");
        Execute("UPDATE workspaces SET state=$state,recorded_head=$head,transition_target=NULL,artifact_fingerprint=$fingerprint,updated_utc=$utc WHERE workspace_id=$id AND state='Transitioning'",
            ("$id", id), ("$state", state.ToString()), ("$head", head), ("$fingerprint", fingerprint));
    }

    /// <inheritdoc/>
    public void Quarantine(string id) => Execute("UPDATE workspaces SET state='Quarantined',transition_target=NULL,updated_utc=$utc WHERE workspace_id=$id", ("$id", id));

    /// <inheritdoc/>
    public void Release(string id) => Execute("UPDATE workspaces SET state='Available',branch=NULL,updated_utc=$utc WHERE workspace_id=$id AND state='Assigned'", ("$id", id));

    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        try
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=20000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Execute(string sql, params (string Name, string? Value)[] values)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string? value) in values) command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Ownership state changed unexpectedly.");
    }
}
