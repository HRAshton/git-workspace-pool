namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>A released empty slot and its retained, verified donor. Selection never releases or modifies the donor.</summary>
public sealed record VerifiedReuseCandidate(
    Guid SlotId,
    long Generation,
    string WorkspacePath,
    Guid DonorSessionId,
    string DonorLabel,
    string DonorHead,
    int ChangedPaths,
    bool CanCopyNpm,
    long? NpmBytes,
    string ArtifactStatus
);

/// <summary>A preview bound to one fetched immutable target. Existing owners bypass pool selection.</summary>
public sealed record GitBranchReusePlan(
    GitBranchRequest Request,
    string? TargetCommit,
    bool ExistingOwner,
    IReadOnlyList<VerifiedReuseCandidate> Candidates
);
