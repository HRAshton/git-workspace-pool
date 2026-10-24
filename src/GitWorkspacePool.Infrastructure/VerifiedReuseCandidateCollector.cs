using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Read-only candidate collection. A candidate owns no user checkout at its empty Ready path.</summary>
public sealed class VerifiedReuseCandidateCollector : IWorkspaceReuseCandidateCollector
{
    private readonly IWorkspaceReadySlotCatalog _slots;
    private readonly IWorkspaceRecoveryJournal _journal;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceParkingOperations _parking;
    private readonly IWorkspaceReadySlotSafety _paths;

    /// <summary>Uses persisted release ownership plus independent Git/filesystem verification.</summary>
    public VerifiedReuseCandidateCollector(
        IWorkspaceReadySlotCatalog slots,
        IWorkspaceRecoveryJournal journal,
        IWorkspaceGit git,
        IWorkspaceParkingOperations parking,
        IWorkspaceReadySlotSafety paths
    )
    {
        _slots = slots;
        _journal = journal;
        _git = git;
        _parking = parking;
        _paths = paths;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VerifiedReuseCandidate>> CollectAsync(
        GitRepositoryRegistration repository, string targetCommit, CancellationToken cancellationToken
    )
    {
        if (targetCommit.Length is not (40 or 64) || !targetCommit.All(Uri.IsHexDigit))
            throw new ArgumentException("A full pinned target commit is required.");
        IReadOnlyList<WorkspaceSlot> slots = await _slots.ListReadyAsync(cancellationToken);
        IReadOnlyList<WorkspaceSession> sessions = await _journal.ListRecoveryParkingSessionsAsync(cancellationToken);
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        WorkspaceSlot[] matching = slots.Where(slot => slot.RepositoryKey.Equals(repository.RepositoryIdentity, StringComparison.Ordinal)
            && Path.GetFullPath(slot.SourcePath).Equals(Path.GetFullPath(repository.LocalPath), paths)).Take(1025).ToArray();
        if (matching.Length > 1024) throw new InvalidOperationException("The matching pool exceeds the candidate limit.");
        List<VerifiedReuseCandidate> verified = [];
        foreach (WorkspaceSlot slot in matching)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _paths.EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, slot.WorkspacePath);
                if (File.Exists(slot.WorkspacePath) || Directory.Exists(slot.WorkspacePath)
                    || await _git.IsRegisteredWorktreeAsync(repository.LocalPath, slot.WorkspacePath, cancellationToken)) continue;
                foreach (WorkspaceSession donor in sessions.Where(session => session.State == WorkspaceSessionState.Released
                    && session.RepositoryKey.Equals(slot.RepositoryKey, StringComparison.Ordinal)
                    && Path.GetFullPath(session.SourcePath).Equals(Path.GetFullPath(repository.LocalPath), paths)
                    && Path.GetFullPath(session.WorkspacePath).Equals(Path.GetFullPath(slot.WorkspacePath), paths)).Take(64))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (donor.ParkingPath is null || donor.ParkingCommitSha is null) continue;
                    string expected = Path.GetFullPath(Path.Combine(repository.WorkspaceRoot, ".gwp-parked", donor.OperationId.ToString("N")));
                    if (!Path.GetFullPath(donor.ParkingPath).Equals(expected, paths)) continue;
                    _parking.EnsureReleasePathsSafe(repository.LocalPath, slot.WorkspacePath, expected);
                    if (!_parking.Exists(expected) || !await _parking.IsRegisteredAsync(repository.LocalPath, expected, cancellationToken)
                        || !string.Equals(await _git.GetWorktreeCommitAsync(expected, cancellationToken),
                            donor.ParkingCommitSha, StringComparison.OrdinalIgnoreCase)) continue;
                    var diff = await BoundedGitProcess.RunAsync(repository.LocalPath,
                        ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z",
                            donor.ParkingCommitSha, targetCommit, "--"], 16 * 1024 * 1024, cancellationToken);
                    if (diff.ExitCode != 0) continue;
                    int changed = diff.Output.Count(character => character == '\0');
                    if (changed > 100000) continue;
                    (bool compatible, long? bytes, string status) = await InspectNpmAsync(
                        repository.LocalPath, expected, targetCommit, cancellationToken);
                    verified.Add(new VerifiedReuseCandidate(slot.SlotId, slot.Generation, slot.WorkspacePath,
                        donor.OperationId, donor.DisplayName, donor.ParkingCommitSha, changed, compatible, bytes, status));
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // An unverifiable candidate is not an empty/safe result. Exclude it; never repair it during selection.
            }
        }
        WarmPoolCandidate[] observations = verified.Select(candidate => new WarmPoolCandidate(
            candidate.SlotId, repository.RepositoryIdentity, candidate.WorkspacePath, candidate.DonorHead,
            WarmPoolSlotState.Ready, false, false, candidate.ChangedPaths, 0, null)).ToArray();
        // All targets are empty and all donors remain retained: selection invalidates zero user artifact bytes.
        return RankWarmPoolCandidates.Rank(repository.RepositoryIdentity, targetCommit, observations)
            .Select(observation => verified.Single(candidate => candidate.SlotId == observation.SlotId)).ToArray();
    }

    private static async Task<(bool Compatible, long? Bytes, string Status)> InspectNpmAsync(
        string repository, string donor, string target, CancellationToken cancellationToken
    )
    {
        string cache = Path.Combine(donor, "node_modules");
        if (!Directory.Exists(cache)) return (false, null, "No npm cache");
        try
        {
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(cache);
            foreach (string name in new[] { "package.json", "package-lock.json" })
            {
                string path = Path.Combine(donor, name);
                WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
                if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return (false, null, "No comparable npm manifests");
                var size = await BoundedGitProcess.RunAsync(repository, ["cat-file", "-s", target + ":" + name], 128, cancellationToken);
                if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), out long length) || length < 0 || length > 4 * 1024 * 1024)
                    return (false, null, "No comparable npm manifests");
                var content = await BoundedGitProcess.RunAsync(repository, ["show", "--no-textconv", target + ":" + name],
                    4 * 1024 * 1024, cancellationToken);
                if (content.ExitCode != 0) return (false, null, "No comparable npm manifests");
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long fileLength = stream.Length;
                if (fileLength > 4 * 1024 * 1024) return (false, null, "npm manifest exceeds comparison limit");
                byte[] bytes = new byte[checked((int)fileLength)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!text.Equals(content.Output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal))
                    return (false, null, "npm manifests differ");
            }
            var ignored = await BoundedGitProcess.RunAsync(donor,
                ["check-ignore", "--quiet", "--no-index", "--", "node_modules/.gwp-probe"], 1024, cancellationToken);
            var tracked = await BoundedGitProcess.RunAsync(donor, ["ls-files", "-z", "--", "node_modules"], 65536, cancellationToken);
            var targetTracked = await BoundedGitProcess.RunAsync(repository,
                ["ls-tree", "-r", "--name-only", target, "--", "node_modules"], 65536, cancellationToken);
            if (ignored.ExitCode != 0 || tracked.ExitCode != 0 || tracked.Output.Length != 0
                || targetTracked.ExitCode != 0 || targetTracked.Output.Length != 0)
                return (false, null, "npm cache must be ignored and untracked");
            long total = 0;
            int files = 0;
            int directories = 0;
            Stack<string> pending = new();
            pending.Push(cache);
            while (pending.TryPop(out string? directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkspaceSnapshotPathPolicy.EnsureNotReparse(directory);
                if (++directories > 30000) return (false, null, "npm directory limit exceeded");
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        return (false, null, "npm cache contains unsupported entries");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else
                    {
                        if (++files > 20000) return (false, null, "npm file limit exceeded");
                        total = checked(total + new FileInfo(entry).Length);
                        if (total > 512L * 1024 * 1024) return (false, null, "npm copy limit exceeded");
                    }
                }
            }
            return files == 0 ? (false, 0, "npm cache is empty") : (true, total, "npm cache available; copy rechecks compatibility");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException or ArgumentException)
        {
            return (false, null, "npm cache could not be verified");
        }
    }
}
