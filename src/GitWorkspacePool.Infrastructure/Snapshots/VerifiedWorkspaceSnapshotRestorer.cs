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
    private readonly IWorkspaceRestoreJournal _restoreJournal;
    private readonly GitWorktreeSnapshotScanner _scanner;
    private readonly ISnapshotCheckpointSink? _checkpoints;
    private readonly ISnapshotDiskSpaceProbe _diskSpace;
    private readonly ISnapshotCopyProgressSink? _copyProgress;

    /// <summary>
    /// Initializes recovery with a private directory outside every managed worktree.
    /// </summary>
    public VerifiedWorkspaceSnapshotRestorer(
        string recoveryRoot,
        IWorkspaceGit git,
        GitWorktreeSnapshotScanner scanner,
        IWorkspaceRestoreJournal restoreJournal,
        ISnapshotCheckpointSink? checkpoints = null,
        ISnapshotDiskSpaceProbe? diskSpace = null,
        ISnapshotCopyProgressSink? copyProgress = null
    )
    {
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
        _git = git;
        _scanner = scanner;
        _restoreJournal = restoreJournal;
        _checkpoints = checkpoints;
        _diskSpace = diskSpace ?? new SystemSnapshotDiskSpaceProbe();
        _copyProgress = copyProgress;
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
            manifest.Version != WorkspaceSnapshotManifest.CurrentVersion
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

        // Validate the complete shape *before* reading payloads, recording a
        // restore intent, or allocating a new Git worktree.
        ValidateManifestPaths(manifest);

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
        WorkspaceSnapshotPathPolicy.EnsureSeparated(
            Path.GetFullPath(session.SourcePath),
            _recoveryRoot
        );
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

        // Conservative preflight before any new SQLite intent or filesystem allocation.
        // Checkout may need more space than the saved changes alone; subsequent
        // I/O failures still retain incomplete recovery artifacts for inspection.
        SnapshotStorageBudget.RequireSpace(
            _diskSpace,
            _recoveryRoot,
            receipt.CopiedBytes
        );

        Guid restoreId = Guid.NewGuid();
        string target = Path.Combine(
            _recoveryRoot,
            "restore-" + receipt.SnapshotId.ToString("N") + "-" + restoreId.ToString("N")
        );

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException("The recovery destination is already occupied.");
        }

        // Persist intent before Git can create or partially create its worktree.
        await _restoreJournal.ReserveRestoreAsync(
            restoreId,
            receipt.SnapshotId,
            target,
            cancellationToken
        );
        Directory.CreateDirectory(_recoveryRoot);
        // Failed attempts remain reserved for manual recovery, never automatically cleaned.
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

        await CheckpointAsync(
            SnapshotCheckpoint.RestoreWorktreeCreated,
            target,
            cancellationToken
        );

        // A new Git worktree must still be a clean checkout before applying
        // preserved user data. Editors, sync tools or other processes can add
        // files after Git returns; never overwrite their unexpected contents.
        WorktreeSnapshotScan checkout = await _scanner.InspectAsync(
            target,
            cancellationToken
        );
        if (
            !string.Equals(
                checkout.Head,
                manifest.HeadCommitSha,
                StringComparison.OrdinalIgnoreCase
            )
            || checkout.Status.Length != 0
        )
        {
            throw new InvalidOperationException(
                "Recovery checkout changed before snapshot files were applied. "
                    + "The isolated worktree was retained for manual inspection."
            );
        }

        // Remember the exact file occupants present at the verified clean
        // checkout. Testing File.Exists only at copy time is unsafe: a foreign
        // file created after checkout could otherwise be mistaken for a
        // tracked checkout file and overwritten by FileMode.Create.
        HashSet<string> filesAtCleanCheckout = PathNames();
        foreach (WorkspaceSnapshotFile file in manifest.Files.Where(item => !item.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeFilePath(file.RelativePath);
            string destination = WorkspaceSnapshotPathPolicy.Resolve(target, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(destination);
            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException(
                    "A recovery file destination is occupied by a directory."
                );
            }

            if (File.Exists(destination))
            {
                filesAtCleanCheckout.Add(relative);
            }
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
            bool existedAtCleanCheckout = filesAtCleanCheckout.Contains(relative);
            if (existedAtCleanCheckout)
            {
                WorkspaceSnapshotPathPolicy.EnsureNotReparse(destination);
                if (!File.Exists(destination))
                {
                    throw new InvalidOperationException(
                        "A tracked recovery file disappeared after checkout."
                    );
                }
            }

            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(source);
            await SnapshotFileCopier.CopyAsync(
                source,
                destination,
                existedAtCleanCheckout ? FileMode.Create : FileMode.CreateNew,
                SnapshotCopyPhase.RestoreWorkingFile,
                _copyProgress,
                cancellationToken
            );
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

        // Apply directory metadata after all descendants are created and copied:
        // creating a child updates its parent's last-write timestamp on Windows.
        foreach (
            WorkspaceSnapshotFile directory in manifest.Files
                .Where(item => item.IsDirectory)
                .OrderByDescending(item => item.RelativePath.Length)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = WorkspaceSnapshotPathPolicy.Resolve(
                target,
                NormalizeFilePath(directory.RelativePath)
            );
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(destination);
            Directory.SetLastWriteTimeUtc(destination, directory.LastWriteUtc);

            FileAttributes allowed = directory.Attributes & ~FileAttributes.ReparsePoint;
            File.SetAttributes(destination, allowed);
        }

        await CheckpointAsync(
            SnapshotCheckpoint.RestoreFilesApplied,
            target,
            cancellationToken
        );

        // Restore original index bytes only into Git's newly allocated admin worktree.
        string newIndexPath = (await _scanner.InspectAsync(target, cancellationToken)).IndexPath;
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(
            Path.GetDirectoryName(newIndexPath)!
        );
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(indexFile);
        WorkspaceSnapshotPathPolicy.EnsureNotReparse(newIndexPath);
        File.Copy(indexFile, newIndexPath, overwrite: true);
        await VerifyFileAsync(
            newIndexPath,
            manifest.GitIndexSha256,
            null,
            cancellationToken
        );

        await CheckpointAsync(
            SnapshotCheckpoint.RestoreIndexApplied,
            target,
            cancellationToken
        );

        WorktreeSnapshotScan restored = await _scanner.InspectAsync(target, cancellationToken);

        if (restored.Head != manifest.HeadCommitSha || restored.Status != expectedStatus)
        {
            throw new InvalidOperationException(
                "The recovered Git status differs from the original snapshot. "
                    + "The isolated recovery worktree was retained for inspection."
            );
        }

        // Verify the preserved index as well as the working file payloads.
        await VerifyFileAsync(
            newIndexPath,
            manifest.GitIndexSha256,
            null,
            cancellationToken
        );

        foreach (WorkspaceSnapshotFile file in manifest.Files.Where(item => !item.IsDirectory))
        {
            string targetFile = WorkspaceSnapshotPathPolicy.Resolve(
                target,
                NormalizeFilePath(file.RelativePath)
            );
            await VerifyFileAsync(targetFile, file.Sha256, file.Length, cancellationToken);
        }

        await _restoreJournal.VerifyRestoreAsync(restoreId, cancellationToken);
        return target;
    }

    private ValueTask CheckpointAsync(
        SnapshotCheckpoint checkpoint,
        string operationPath,
        CancellationToken cancellationToken
    ) => _checkpoints?.OnCheckpointAsync(
        checkpoint,
        operationPath,
        cancellationToken
    ) ?? ValueTask.CompletedTask;

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
        try
        {
            WorkspaceSnapshotManifest? manifest = await JsonSerializer.DeserializeAsync<WorkspaceSnapshotManifest>(
                stream,
                cancellationToken: cancellationToken
            );
            return manifest ?? throw new InvalidOperationException("Snapshot manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Snapshot manifest is malformed or has no explicit version.",
                exception
            );
        }
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

    private static void ValidateManifestPaths(WorkspaceSnapshotManifest manifest)
    {
        // Independent staged payload paths may overlap working files, but paths
        // within one tree must never alias or put a file above another entry.
        HashSet<string> workingNames = PathNames();
        HashSet<string> workingFiles = PathNames();
        HashSet<string> stagedNames = PathNames();
        HashSet<string> missingNames = PathNames();

        foreach (WorkspaceSnapshotFile item in manifest.Files)
        {
            if (item is null)
            {
                throw new InvalidOperationException("Snapshot manifest contains a null file.");
            }

            string path = WorkspaceSnapshotPathPolicy.NormalizeRelative(item.RelativePath);
            if (!workingNames.Add(path))
            {
                throw new InvalidOperationException("Duplicate snapshot working path.");
            }

            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Snapshot metadata contains a reparse point.");
            }

            if (item.IsDirectory)
            {
                if (item.Sha256 is not null || item.Length != 0)
                {
                    throw new InvalidOperationException("Snapshot directory metadata is invalid.");
                }
            }
            else
            {
                if ((item.Attributes & FileAttributes.Directory) != 0 || item.Length < 0)
                {
                    throw new InvalidOperationException("Snapshot file metadata is invalid.");
                }

                workingFiles.Add(path);
            }
        }

        foreach (WorkspaceSnapshotFile item in manifest.StagedFiles)
        {
            if (item is null)
            {
                throw new InvalidOperationException("Snapshot manifest contains a null staged file.");
            }

            string path = WorkspaceSnapshotPathPolicy.NormalizeRelative(item.RelativePath);
            if (
                !stagedNames.Add(path)
                || item.IsDirectory
                || item.Length < 0
                || (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
            )
            {
                throw new InvalidOperationException("Invalid staged snapshot path or metadata.");
            }
        }

        foreach (string path in manifest.MissingPaths)
        {
            string normalized = WorkspaceSnapshotPathPolicy.NormalizeRelative(path);
            if (!missingNames.Add(normalized) || workingNames.Contains(normalized))
            {
                throw new InvalidOperationException("Conflicting snapshot deletion path.");
            }
        }

        workingFiles.UnionWith(missingNames);
        HashSet<string> allWorkingPaths = PathNames();
        allWorkingPaths.UnionWith(workingNames);
        allWorkingPaths.UnionWith(missingNames);
        RejectFileAncestors(workingFiles, allWorkingPaths);
        RejectFileAncestors(stagedNames, stagedNames);
    }

    private static void RejectFileAncestors(
        HashSet<string> filePaths,
        HashSet<string> allPaths
    )
    {
        foreach (string path in allPaths)
        {
            int separator = path.IndexOf('/');
            while (separator >= 0)
            {
                if (filePaths.Contains(path[..separator]))
                {
                    throw new InvalidOperationException(
                        "Snapshot manifest has a file/directory ancestor collision."
                    );
                }

                separator = path.IndexOf('/', separator + 1);
            }
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

        return WorkspaceSnapshotPathPolicy.NormalizeRelative(value);
    }
}
