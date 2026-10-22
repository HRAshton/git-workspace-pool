using System.Security.Cryptography;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Bounded read-only evidence check for previously verified isolated restores.
/// It never authorizes adoption, repair, release or deletion.
/// </summary>
internal static class WorkspaceRecoveryVerifier
{
    // Diagnostics must not hash a multi-gigabyte snapshot on every tray refresh.
    // Larger payloads are explicitly unverifiable, never optimistically verified.
    private const long MaxVerificationBytes = 64L * 1024 * 1024;
    private const int MaxVerificationEntries = 4096;

    public static async Task<bool> MatchesAsync(
        string recoveryPath,
        string snapshotFolder,
        WorkspaceSnapshotManifest manifest,
        CancellationToken cancellationToken
    )
    {
        if (
            manifest.Files is null
            || manifest.MissingPaths is null
            || manifest.StagedFiles is null
            || manifest.Files.Count > MaxVerificationEntries
            || manifest.MissingPaths.Count > MaxVerificationEntries
            || manifest.StagedFiles.Count > MaxVerificationEntries
            || !ValidHash(manifest.GitIndexSha256)
        )
        {
            throw new InvalidOperationException(
                "Snapshot evidence cannot be checked within the recovery inventory limits."
            );
        }

        GitWorktreeSnapshotScanner scanner = new();
        WorktreeSnapshotScan before = await scanner.InspectAsync(
            recoveryPath,
            cancellationToken
        );
        if (!MatchesGitState(before, manifest))
        {
            return false;
        }

        long checkedBytes = 0;
        if (
            !await MatchesHashAsync(
                before.IndexPath,
                manifest.GitIndexSha256,
                null,
                cancellationToken,
                bytes => checkedBytes = AddVerifiedBytes(checkedBytes, bytes)
            )
        )
        {
            return false;
        }

        foreach (WorkspaceSnapshotFile file in manifest.StagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null || file.IsDirectory || file.Length < 0 || !ValidHash(file.Sha256))
            {
                throw new InvalidOperationException(
                    "Saved staged snapshot evidence is invalid."
                );
            }

            string staged = WorkspaceSnapshotPathPolicy.Resolve(
                Path.Combine(snapshotFolder, "staged"),
                file.RelativePath
            );
            if (
                !await MatchesHashAsync(
                    staged,
                    file.Sha256!,
                    file.Length,
                    cancellationToken,
                    bytes => checkedBytes = AddVerifiedBytes(checkedBytes, bytes)
                )
            )
            {
                // A damaged saved blob means the snapshot is not a trustworthy
                // recovery source, even if this particular restored copy still
                // matches its working tree.
                throw new InvalidOperationException(
                    "Saved staged snapshot payload is missing or damaged."
                );
            }
        }

        foreach (WorkspaceSnapshotFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null)
            {
                throw new InvalidOperationException("Snapshot contains invalid file evidence.");
            }

            string path = WorkspaceSnapshotPathPolicy.Resolve(
                recoveryPath,
                file.RelativePath
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);

            if (file.IsDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return false;
                }

                continue;
            }

            if (file.Length < 0 || !ValidHash(file.Sha256))
            {
                throw new InvalidOperationException("Snapshot file evidence is invalid.");
            }

            if (
                !await MatchesHashAsync(
                    path,
                    file.Sha256!,
                    file.Length,
                    cancellationToken,
                    bytes => checkedBytes = AddVerifiedBytes(checkedBytes, bytes)
                )
            )
            {
                return false;
            }
        }

        if (!MatchesRecordedDirectories(recoveryPath, manifest, cancellationToken))
        {
            return false;
        }

        foreach (string relative in manifest.MissingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = WorkspaceSnapshotPathPolicy.Resolve(recoveryPath, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return false;
            }
        }

        // If index or Git status changes during hashing, do not label this
        // recovery worktree as currently matching the original snapshot.
        WorktreeSnapshotScan after = await scanner.InspectAsync(
            recoveryPath,
            cancellationToken
        );
        if (
            !MatchesGitState(after, manifest)
            || !string.Equals(
                before.IndexPath,
                after.IndexPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        return await MatchesHashAsync(
            after.IndexPath,
            manifest.GitIndexSha256,
            null,
            cancellationToken,
            null
        );
    }

    // Export captures every descendant under a recorded changed directory,
    // including ignored and untracked files. Git's --ignored=matching status
    // can collapse a whole directory into one entry, so hashing only manifest
    // files would miss unexpected new files placed underneath it.
    private static bool MatchesRecordedDirectories(
        string recoveryPath,
        WorkspaceSnapshotManifest manifest,
        CancellationToken cancellationToken
    )
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Dictionary<string, HashSet<string>> children = new(comparer);
        HashSet<string> recordedDirectories = new(comparer);

        foreach (WorkspaceSnapshotFile file in manifest.Files)
        {
            string normalized = WorkspaceSnapshotPathPolicy.NormalizeRelative(
                file.RelativePath
            );
            if (file.IsDirectory)
            {
                recordedDirectories.Add(normalized);
                children.Add(normalized, new HashSet<string>(comparer));
            }
        }

        foreach (WorkspaceSnapshotFile file in manifest.Files)
        {
            string normalized = WorkspaceSnapshotPathPolicy.NormalizeRelative(
                file.RelativePath
            );
            int separator = normalized.LastIndexOf('/');
            if (separator >= 0)
            {
                string parent = normalized[..separator];
                if (children.TryGetValue(parent, out HashSet<string>? names))
                {
                    if (!names.Add(normalized[(separator + 1)..]))
                    {
                        throw new InvalidOperationException(
                            "Snapshot contains duplicate directory descendants."
                        );
                    }
                }
            }
        }

        int scanned = 0;
        foreach (string relative in recordedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = WorkspaceSnapshotPathPolicy.Resolve(
                recoveryPath,
                relative
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(directory);
            HashSet<string> expected = children[relative];
            HashSet<string> found = new(comparer);

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaxVerificationEntries)
                {
                    throw new InvalidOperationException(
                        "Recovery directory inventory exceeds the bounded entry limit."
                    );
                }

                string name = Path.GetFileName(entry);
                if (!expected.Contains(name) || !found.Add(name))
                {
                    return false;
                }

                WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(entry);
            }

            if (found.Count != expected.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesGitState(
        WorktreeSnapshotScan scan,
        WorkspaceSnapshotManifest manifest
    ) =>
        string.Equals(
            scan.Head,
            manifest.HeadCommitSha,
            StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(scan.Status)),
            manifest.StatusBase64,
            StringComparison.Ordinal
        );

    private static long AddVerifiedBytes(long previous, long current)
    {
        long total = checked(previous + current);
        if (total > MaxVerificationBytes)
        {
            throw new InvalidOperationException(
                "Recovery payload exceeds the bounded read-only verification budget."
            );
        }

        return total;
    }

    private static bool ValidHash(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static async Task<bool> MatchesHashAsync(
        string path,
        string expected,
        long? expectedLength,
        CancellationToken cancellationToken,
        Action<long>? countBytes
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        FileInfo file = new(path);
        if (!file.Exists || (expectedLength.HasValue && file.Length != expectedLength))
        {
            return false;
        }

        countBytes?.Invoke(file.Length);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan
        );
        string hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
        );
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
