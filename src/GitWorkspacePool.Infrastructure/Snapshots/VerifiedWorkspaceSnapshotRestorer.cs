using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Reconstructs a verified Git worktree side by side with its original, without touching user data.
/// </summary>
public sealed class VerifiedWorkspaceSnapshotRestorer : IWorkspaceSnapshotRestorer
{
    private readonly string _recoveryRoot;
    private readonly IWorkspaceGit _git;
    private readonly GitWorktreeSnapshotScanner _scanner;

    /// <summary>
    /// Initializes recovery with a private directory outside every managed worktree.
    /// </summary>
    public VerifiedWorkspaceSnapshotRestorer(
        string recoveryRoot,
        IWorkspaceGit git,
        GitWorktreeSnapshotScanner scanner
    )
    {
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
        _git = git;
        _scanner = scanner;
    }

    /// <inheritdoc/>
    public async Task<string> RestoreAsync(
        WorkspaceSnapshotRecord record,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSession session = record.Session;
        WorkspaceSnapshotReceipt receipt = record.Receipt;
        string snapshot = Path.GetFullPath(receipt.Folder);
        string origin = Path.GetFullPath(session.WorkspacePath);

        if (session.OperationId != receipt.OperationId)
        {
            throw new InvalidOperationException("Snapshot owner does not match its journal entry.");
        }

        WorkspaceSnapshotPathPolicy.EnsureSeparated(origin, _recoveryRoot);
        WorkspaceSnapshotPathPolicy.EnsureSeparated(snapshot, _recoveryRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(snapshot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_recoveryRoot);

        WorkspaceSnapshotManifest manifest = await ReadManifestAsync(snapshot, cancellationToken);

        if (
            manifest.Version != 2
            || manifest.SnapshotId != receipt.SnapshotId
            || manifest.OperationId != session.OperationId
            || !Path.GetFullPath(manifest.WorkspacePath).Equals(
                origin,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || !ValidCommitId(manifest.HeadCommitSha)
        )
        {
            throw new InvalidOperationException(
                "Snapshot manifest version, ownership, or original Git identity is invalid."
            );
        }

        if (
            manifest.Files is null
            || manifest.StagedFiles is null
            || manifest.MissingPaths is null
            || string.IsNullOrWhiteSpace(manifest.StatusBase64)
            || !ValidSha256(manifest.GitIndexSha256)
        )
        {
            throw new InvalidOperationException("Snapshot manifest is incomplete.");
        }

        byte[] serializedStatus;

        try
        {
            serializedStatus = Convert.FromBase64String(manifest.StatusBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Snapshot Git status is not valid Base64.", exception);
        }

        string expectedStatus = new UTF8Encoding(false, true).GetString(serializedStatus);
        string indexFile = Path.Combine(snapshot, "index.bin");
        await VerifyFileAsync(indexFile, manifest.GitIndexSha256, null, cancellationToken);

        HashSet<string> workingNames = PathNames();
        HashSet<string> stagedNames = PathNames();
        long totalBytes = new FileInfo(indexFile).Length;
        int fileCount = 1;

        foreach (WorkspaceSnapshotFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeFilePath(file.RelativePath);
            string source = WorkspaceSnapshotPathPolicy.Resolve(
                Path.Combine(snapshot, "files"),
                relative
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(source);

            if (!workingNames.Add(relative))
            {
                throw new InvalidOperationException("Duplicate snapshot working path.");
            }

            if (file.IsDirectory)
            {
                if (!Directory.Exists(source) || file.Sha256 is not null)
                {
                    throw new InvalidOperationException("Snapshot directory metadata is corrupt.");
                }

                continue;
            }

            await VerifyFileAsync(source, file.Sha256, file.Length, cancellationToken);
            totalBytes = checked(totalBytes + file.Length);
            fileCount++;
        }

        foreach (WorkspaceSnapshotFile file in manifest.StagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeFilePath(file.RelativePath);
            string source = WorkspaceSnapshotPathPolicy.Resolve(
                Path.Combine(snapshot, "staged"),
                relative
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(source);

            if (file.IsDirectory || !stagedNames.Add(relative))
            {
                throw new InvalidOperationException("Duplicate or invalid staged snapshot path.");
            }

            await VerifyFileAsync(source, file.Sha256, file.Length, cancellationToken);
            totalBytes = checked(totalBytes + file.Length);
            fileCount++;
        }

        HashSet<string> missingNames = PathNames();

        foreach (string path in manifest.MissingPaths)
        {
            string relative = NormalizeFilePath(path);

            if (!missingNames.Add(relative) || workingNames.Contains(relative))
            {
                throw new InvalidOperationException("Conflicting snapshot deletion path.");
            }
        }

        if (fileCount != receipt.FileCount || totalBytes != receipt.CopiedBytes)
        {
            throw new InvalidOperationException("Snapshot journal counters do not match its payload.");
        }

        Directory.CreateDirectory(_recoveryRoot);
        string target = Path.Combine(
            _recoveryRoot,
            "restore-" + receipt.SnapshotId.ToString("N") + "-" + Guid.NewGuid().ToString("N")
        );

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException("The recovery destination is already occupied.");
        }

        // The destination name is app-generated and never comes from a browser or manifest.
        // A failed restoration intentionally remains isolated for manual inspection.
        await _git.CreateWorktreeAsync(
            session.SourcePath,
            target,
            manifest.HeadCommitSha,
            cancellationToken
        );

        if (
            !await _git.IsRegisteredWorktreeAsync(
                session.SourcePath,
                target,
                cancellationToken
            )
            || !string.Equals(
                await _git.GetWorktreeCommitAsync(target, cancellationToken),
                manifest.HeadCommitSha,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException("New recovery worktree failed Git validation.");
        }

        foreach (WorkspaceSnapshotFile file in manifest.Files.Where(item => item.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeFilePath(file.RelativePath);
            string destination = WorkspaceSnapshotPathPolicy.Resolve(target, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(destination);
            Directory.CreateDirectory(destination);
        }

        foreach (WorkspaceSnapshotFile file in manifest.Files.Where(item => !item.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeFilePath(file.RelativePath);
            string destination = WorkspaceSnapshotPathPolicy.Resolve(target, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(
                Path.GetDirectoryName(destination)!
            );
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string source = WorkspaceSnapshotPathPolicy.Resolve(
                Path.Combine(snapshot, "files"),
                relative
            );
            File.Copy(source, destination, overwrite: true);
            File.SetAttributes(destination, file.Attributes);
            File.SetLastWriteTimeUtc(destination, file.LastWriteUtc);
        }

        foreach (string missing in manifest.MissingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = WorkspaceSnapshotPathPolicy.Resolve(
                target,
                NormalizeFilePath(missing)
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(
                Path.GetDirectoryName(destination)!
            );

            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException(
                    "Directory deletions require manual recovery; no existing tree was modified."
                );
            }

            if (File.Exists(destination))
            {
                WorkspaceSnapshotPathPolicy.EnsureNotReparse(destination);
                File.SetAttributes(destination, FileAttributes.Normal);
                File.Delete(destination);
            }
        }

        // Restore original index bytes only into Git's newly allocated admin worktree.
        string newIndexPath = (await _scanner.InspectAsync(target, cancellationToken)).IndexPath;
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(
            Path.GetDirectoryName(newIndexPath)!
        );
        File.Copy(indexFile, newIndexPath, overwrite: true);

        WorktreeSnapshotScan restored = await _scanner.InspectAsync(target, cancellationToken);

        if (restored.Head != manifest.HeadCommitSha || restored.Status != expectedStatus)
        {
            throw new InvalidOperationException(
                "The recovered Git status differs from the original snapshot. "
                    + "The isolated recovery worktree was retained for inspection."
            );
        }

        // Verifies copy output after restoring the Git index.
        foreach (WorkspaceSnapshotFile file in manifest.Files.Where(item => !item.IsDirectory))
        {
            string targetFile = WorkspaceSnapshotPathPolicy.Resolve(
                target,
                NormalizeFilePath(file.RelativePath)
            );
            await VerifyFileAsync(targetFile, file.Sha256, file.Length, cancellationToken);
        }

        return target;
    }

    private static async Task<WorkspaceSnapshotManifest> ReadManifestAsync(
        string snapshot,
        CancellationToken cancellationToken
    )
    {
        string path = Path.Combine(snapshot, "manifest.json");
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        WorkspaceSnapshotManifest? manifest = await JsonSerializer.DeserializeAsync<WorkspaceSnapshotManifest>(
            stream,
            cancellationToken: cancellationToken
        );
        return manifest ?? throw new InvalidOperationException("Snapshot manifest is empty.");
    }

    private static async Task VerifyFileAsync(
        string path,
        string? expectedHash,
        long? expectedLength,
        CancellationToken cancellationToken
    )
    {
        if (!ValidSha256(expectedHash))
        {
            throw new InvalidOperationException("Snapshot file has an invalid SHA-256 digest.");
        }

        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        FileInfo info = new(path);

        if (
            !info.Exists
            || (expectedLength.HasValue && expectedLength.Value != info.Length)
        )
        {
            throw new InvalidOperationException("Snapshot file is missing or has unexpected length.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan
        );
        string actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
        );

        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Snapshot checksum verification failed.");
        }
    }

    private static HashSet<string> PathNames() => new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidCommitId(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static string NormalizeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Snapshot path must not be empty.");
        }

        return value.Replace('\\', '/');
    }
}
