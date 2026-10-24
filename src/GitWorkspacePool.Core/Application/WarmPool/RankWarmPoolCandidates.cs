using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Deterministic, side-effect-free warm slot ranking. This component never moves or
/// releases a worktree; the allocator must independently recheck ownership and Git.
/// </summary>
public static class RankWarmPoolCandidates
{
    private const int MaxCandidates = 1024;

    /// <summary>
    /// Filters unsafe/unowned slots and orders credible candidates by preparation cost.
    /// </summary>
    /// <param name="repositoryKey">The repository requested by the PR.</param>
    /// <param name="targetCommitSha">Exact commit pinned for the PR.</param>
    /// <param name="candidates">Bounded observations collected by trusted adapters.</param>
    /// <returns>An ordered shortlist; an empty list means use a fresh worktree.</returns>
    public static IReadOnlyList<WarmPoolCandidate> Rank(
        string repositoryKey,
        string targetCommitSha,
        IReadOnlyList<WarmPoolCandidate> candidates
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryKey);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!IsFullGitHash(targetCommitSha))
        {
            throw new ArgumentException("A full pinned commit SHA is required.", nameof(targetCommitSha));
        }

        if (candidates.Count > MaxCandidates)
        {
            throw new InvalidOperationException("The warm pool exceeds its safe ranking limit.");
        }

        return candidates
            .Where(candidate =>
                candidate.SlotId != Guid.Empty
                && candidate.State == WarmPoolSlotState.Ready
                && !candidate.Pinned
                && !candidate.HasUserChanges
                && candidate.RepositoryKey.Equals(repositoryKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.WorktreePath)
                && IsFullGitHash(candidate.HeadCommitSha)
                && candidate.EstimatedChangedPaths >= 0
                && candidate.EstimatedInvalidatedArtifactBytes >= 0
                && (candidate.ObservedTransitionTime is null
                    || candidate.ObservedTransitionTime.Value >= TimeSpan.Zero)
            )
            // Prefer a matching captured head; an empty released slot still needs a fresh checkout.
            .OrderBy(candidate => !candidate.HeadCommitSha.Equals(
                targetCommitSha, StringComparison.OrdinalIgnoreCase
            ))
            // Avoid guessed relative weights between path counts and artifact sizes:
            // compare independently observable costs before optional timing hints.
            .ThenBy(candidate => candidate.EstimatedInvalidatedArtifactBytes)
            .ThenBy(candidate => candidate.EstimatedChangedPaths)
            .ThenBy(candidate => candidate.ObservedTransitionTime?.Ticks ?? long.MaxValue)
            .ThenBy(candidate => candidate.SlotId)
            .ToArray();
    }

    private static bool IsFullGitHash(string? sha) =>
        sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit);
}
