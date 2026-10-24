using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>A bounded readout of one explicitly configured baseline fetch.</summary>
public sealed record BaselineRefreshStatus(string CloneUrl, string Branch, DateTimeOffset AttemptedAt, bool Succeeded, string? Commit, bool InProgress = false);

/// <summary>A bounded sequential refresh pass; partial remote failures are shown in individual statuses.</summary>
public sealed record BaselineRefreshBatch(IReadOnlyList<BaselineRefreshStatus> Attempts, bool AlreadyRunning, bool MoreDue);

/// <summary>Refreshes a bounded number of configured refs without prediction, checkout, cleanup or project execution.</summary>
public sealed class RefreshConfiguredBaselinesService
{
    private readonly IGitRemoteReader _remotes;
    private readonly IGitBaselineFetcher _fetch;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, BaselineRefreshStatus> _latest = new(StringComparer.Ordinal);

    /// <summary>Uses the same trusted Git origin and non-interactive fetch boundaries as opening a workspace.</summary>
    public RefreshConfiguredBaselinesService(IGitRemoteReader remotes, IGitBaselineFetcher fetch)
    {
        _remotes = remotes;
        _fetch = fetch;
    }

    /// <summary>Returns a snapshot of the bounded status inventory.</summary>
    public IReadOnlyList<BaselineRefreshStatus> Latest()
    {
        lock (_gate) return _latest.Values.OrderBy(value => value.CloneUrl).ThenBy(value => value.Branch).ToArray();
    }

    /// <summary>Validates resource bounds and exact branch names before any scheduled fetch.</summary>
    public static void Validate(IReadOnlyList<GitRepositoryRegistration> repositories, int intervalMinutes)
    {
        if (intervalMinutes is < 15 or > 1440) throw new InvalidOperationException("Baseline refresh interval must be 15 to 1440 minutes.");
        if (repositories.Count > 64) throw new InvalidOperationException("At most 64 Git repositories can be configured.");
        foreach (GitRepositoryRegistration repository in repositories)
        {
            _ = GitBranchRequest.NormalizeCloneUrl(repository.CloneUrl);
            if (!Path.IsPathFullyQualified(repository.LocalPath) || !Path.IsPathFullyQualified(repository.WorkspaceRoot))
                throw new InvalidOperationException("Baseline source and workspace paths must be absolute.");
            if (repository.BaselineBranches is null || repository.BaselineBranches.Count > 16
                || repository.BaselineBranches.Any(branch => !GitBranchRequest.IsSafeBranch(branch))
                || repository.BaselineBranches.Distinct(StringComparer.Ordinal).Count() != repository.BaselineBranches.Count)
            {
                throw new InvalidOperationException("Configure at most 16 unique exact baseline branch names per repository; wildcards are not accepted.");
            }
        }
    }

    /// <summary>Fetches at most four due refs, one at a time, with a two-minute per-ref deadline.</summary>
    public async Task<BaselineRefreshBatch> RefreshDueAsync(
        IReadOnlyList<GitRepositoryRegistration> repositories,
        int intervalMinutes,
        DateTimeOffset now,
        bool force,
        CancellationToken cancellationToken
    )
    {
        Validate(repositories, intervalMinutes);
        if (!await _running.WaitAsync(0, cancellationToken)) return new([], true, false);
        try
        {
            var configured = repositories.SelectMany(repository => repository.BaselineBranches.Select(branch =>
                (Repository: repository, Branch: branch, Key: GitBranchRequest.NormalizeCloneUrl(repository.CloneUrl) + "\0" + branch))).ToArray();
            HashSet<string> keys = configured.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (string stale in _latest.Keys.Where(key => !keys.Contains(key)).ToArray()) _latest.Remove(stale);
            }
            var due = configured.Where(item =>
            {
                lock (_gate) return force || !_latest.TryGetValue(item.Key, out BaselineRefreshStatus? status)
                    || now - status.AttemptedAt >= TimeSpan.FromMinutes(intervalMinutes);
            }).ToArray();
            List<BaselineRefreshStatus> attempts = [];
            foreach (var item in due.Take(4))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BaselineRefreshStatus status = new(item.Repository.CloneUrl, item.Branch, now, false, null, true);
                // Failed attempts also observe the refresh interval, avoiding retry storms.
                lock (_gate) _latest[item.Key] = status;
                try
                {
                    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    string origin = await _remotes.GetOriginUrlAsync(item.Repository.LocalPath, deadline.Token);
                    if (!GitBranchRequest.OriginMatches(origin, item.Repository.CloneUrl))
                        throw new InvalidOperationException("Configured baseline origin does not match.");
                    string commit = await _fetch.FetchBaselineCommitAsync(item.Repository.LocalPath, item.Branch, deadline.Token);
                    if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
                        throw new InvalidOperationException("Baseline fetch did not return an immutable commit.");
                    status = status with { Succeeded = true, Commit = commit };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                finally
                {
                    status = status with { InProgress = false };
                    lock (_gate) _latest[item.Key] = status;
                }
                attempts.Add(status);
            }
            return new BaselineRefreshBatch(attempts, false, due.Length > 4);
        }
        finally { _running.Release(); }
    }
}
