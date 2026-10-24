using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>Previews explicitly released-slot reuse. Existing branch owners are always preferred.</summary>
public sealed class PlanGitBranchReuseService
{
    private readonly IGitBranchCatalog _catalog;
    private readonly IGitBranchSessionStore _sessions;
    private readonly IGitRemoteReader _remotes;
    private readonly IGitBranchPreview _preview;
    private readonly IWorkspaceReuseCandidateCollector _candidates;

    /// <summary>Composes trusted configuration, head fetching and a read-only candidate collector.</summary>
    public PlanGitBranchReuseService(
        IGitBranchCatalog catalog,
        IGitBranchSessionStore sessions,
        IGitRemoteReader remotes,
        IGitBranchPreview preview,
        IWorkspaceReuseCandidateCollector candidates
    )
    {
        _catalog = catalog;
        _sessions = sessions;
        _remotes = remotes;
        _preview = preview;
        _candidates = candidates;
    }

    /// <summary>Returns a commit-bound selection preview; it never parks, releases, copies or checks out files.</summary>
    public async Task<GitBranchReusePlan> PlanAsync(GitBranchRequest request, CancellationToken cancellationToken)
    {
        if (!request.IsValid) throw new ArgumentException("Invalid Git repository or branch.");
        GitRepositoryRegistration repository = _catalog.Find(request)
            ?? throw new InvalidOperationException("Configure this Git repository first.");
        if (!GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl)
            || !GitBranchRequest.OriginMatches(
                await _remotes.GetOriginUrlAsync(repository.LocalPath, cancellationToken), repository.CloneUrl))
        {
            throw new InvalidOperationException("The source clone does not match the requested repository.");
        }
        if (await _sessions.FindAsync(request, cancellationToken) is not null)
        {
            return new GitBranchReusePlan(request, null, true, Array.Empty<VerifiedReuseCandidate>());
        }
        string target = await _preview.FetchBranchPreviewAsync(repository.LocalPath, request.Branch, cancellationToken);
        if (target.Length is not (40 or 64) || !target.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The preview did not resolve an immutable commit.");
        }
        IReadOnlyList<VerifiedReuseCandidate> candidates = await _candidates.CollectAsync(repository, target, cancellationToken);
        return new GitBranchReusePlan(request, target, false, candidates);
    }
}
