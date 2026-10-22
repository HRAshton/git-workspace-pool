using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain.WarmPool;
using Xunit;

namespace GitWorkspacePool.Tests;

/// <summary>
/// Selection policy must not grant ownership of data-bearing or protected worktrees.
/// </summary>
public sealed class WarmPoolCandidateRankingTests
{
    private const string Repository = "github:owner/repo";
    private static readonly string Target = new('a', 40);

    [Fact]
    public void Rank_PrefersExactCommitThenLowerArtifactInvalidation()
    {
        WarmPoolCandidate distant = Slot('1', artifacts: 50, paths: 10);
        WarmPoolCandidate nearest = Slot('2', artifacts: 10, paths: 30);
        WarmPoolCandidate exact = Slot('a', artifacts: 999, paths: 500);

        IReadOnlyList<WarmPoolCandidate> ranked = RankWarmPoolCandidates.Rank(
            Repository, Target, [distant, nearest, exact]
        );

        Assert.Equal([exact, nearest, distant], ranked);
    }

    [Fact]
    public void Rank_ExcludesProtectedDirtyBusyUnknownAndOtherRepositories()
    {
        WarmPoolCandidate available = Slot('1');
        WarmPoolCandidate[] candidates =
        [
            Slot('2') with { Pinned = true },
            Slot('3') with { HasUserChanges = true },
            Slot('4') with { State = WarmPoolSlotState.Allocated },
            Slot('5') with { State = WarmPoolSlotState.Transitioning },
            Slot('6') with { State = WarmPoolSlotState.RecoveryRequired },
            Slot('7') with { RepositoryKey = "github:other/repo" },
            Slot('8') with { EstimatedChangedPaths = -1 },
            Slot('9') with { EstimatedInvalidatedArtifactBytes = -1 },
            Slot('b') with { HeadCommitSha = "unknown" },
            Slot('c') with { ObservedTransitionTime = TimeSpan.FromMilliseconds(-1) },
            available,
        ];

        Assert.Equal(
            [available],
            RankWarmPoolCandidates.Rank(Repository, Target, candidates)
        );
    }

    [Fact]
    public void Rank_IsDeterministicAndDoesNotChangeCandidates()
    {
        WarmPoolCandidate first = Slot('1');
        WarmPoolCandidate second = Slot('2');
        WarmPoolCandidate[] source = [second, first];

        IReadOnlyList<WarmPoolCandidate> ranked = RankWarmPoolCandidates.Rank(
            Repository, Target, source
        );

        Assert.Equal(source.OrderBy(candidate => candidate.SlotId), ranked);
        Assert.Equal([second, first], source);
    }

    [Fact]
    public void Rank_RejectsUnpinnedCommitAndUnboundedInput()
    {
        Assert.Throws<ArgumentException>(
            () => RankWarmPoolCandidates.Rank(Repository, "main", [])
        );
        WarmPoolCandidate candidate = Slot('1');
        Assert.Throws<InvalidOperationException>(
            () => RankWarmPoolCandidates.Rank(
                Repository, Target, Enumerable.Repeat(candidate, 1025).ToArray()
            )
        );
    }

    private static WarmPoolCandidate Slot(
        char seed,
        long artifacts = 0,
        int paths = 0
    ) => new(
        Guid.Parse(seed.ToString() + "0000000-0000-0000-0000-000000000001"),
        Repository,
        "C:/workspaces/warm-" + seed,
        new string(seed, 40),
        WarmPoolSlotState.Ready,
        false,
        false,
        paths,
        artifacts,
        null
    );
}
