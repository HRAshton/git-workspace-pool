using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Maps case-sensitive Git branches onto the shared session, snapshot and slot lifecycle.</summary>
public sealed class SqliteGitBranchSessionStore : IGitBranchSessionStore
{
    private readonly SqliteWorkspaceSessionJournal _journal;
    private readonly string _connectionString;

    /// <summary>Upgrades legacy branch ownership transactionally without moving any worktree.</summary>
    public SqliteGitBranchSessionStore(string databasePath)
    {
        _journal = new SqliteWorkspaceSessionJournal(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <inheritdoc/>
    public async Task<GitBranchSession?> FindAsync(
        GitBranchRequest request,
        CancellationToken cancellationToken
    )
    {
        int? reference = await _journal.GetBranchSubjectAsync(request, false, cancellationToken);
        if (reference is null)
        {
            return null;
        }

        WorkspaceSession? session = await _journal.FindByPullRequestAsync(
            request.RepositoryIdentity, reference.Value, cancellationToken
        );
        return session is null ? null : Map(request, session);
    }

    /// <inheritdoc/>
    public async Task<int> GetReferenceNumberAsync(
        GitBranchRequest request,
        CancellationToken cancellationToken
    ) => (await _journal.GetBranchSubjectAsync(request, true, cancellationToken))!.Value;

    /// <inheritdoc/>
    public async Task<GitBranchSession> ReserveAsync(
        GitBranchRequest request,
        GitRepositoryRegistration repository,
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!request.IsValid || !GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl))
        {
            throw new ArgumentException("Invalid branch reservation identity.");
        }

        int reference = await GetReferenceNumberAsync(request, cancellationToken);
        WorkspaceSession session = await _journal.GetOrCreateAsync(
            request.RepositoryIdentity, reference, repository.LocalPath, path, cancellationToken
        );
        return Map(request, session);
    }

    /// <inheritdoc/>
    public Task PinAsync(Guid operationId, string commit, CancellationToken cancellationToken) =>
        _journal.PinCommitAsync(operationId, commit, cancellationToken);

    /// <inheritdoc/>
    public Task ActivateAsync(Guid operationId, CancellationToken cancellationToken) =>
        _journal.ActivateAsync(operationId, cancellationToken);

    /// <inheritdoc/>
    public Task ResumeAsync(Guid operationId, CancellationToken cancellationToken) =>
        _journal.ResumeAsync(operationId, cancellationToken);

    /// <inheritdoc/>
    public Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken) =>
        _journal.RequireRecoveryAsync(operationId, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<GitBranchSession>> ListUnfinishedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.operation_id, b.clone_url, b.branch, s.source_path, s.workspace_path,
                   s.state, s.pinned_commit, s.parking_path, s.parking_head
            FROM sessions s JOIN git_branch_subjects b
              ON b.repository_key = s.repository_key AND b.subject_number = s.pr_number
            WHERE s.state IN ('Preparing', 'RecoveryRequired', 'Parking', 'Unparking')
            ORDER BY b.clone_url, b.branch LIMIT 101;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<GitBranchSession> sessions = [];
        while (reader.Read())
        {
            sessions.Add(new GitBranchSession(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                Enum.Parse<WorkspaceSessionState>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }
        return Task.FromResult<IReadOnlyList<GitBranchSession>>(sessions);
    }

    private static GitBranchSession Map(GitBranchRequest request, WorkspaceSession session) => new(
        session.OperationId, GitBranchRequest.NormalizeCloneUrl(request.CloneUrl), request.Branch,
        session.SourcePath, session.WorkspacePath, session.State, session.PinnedCommitSha,
        session.ParkingPath, session.ParkingCommitSha
    );
}
