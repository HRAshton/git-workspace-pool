using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Copies only changed paths and the Git index, verifies sources, then atomically publishes a manifest.
/// </summary>
public sealed class VerifiedWorkspaceSnapshotExporter : IWorkspaceSnapshotExporter
{
    private const int MaxSnapshotFiles = 50000;
    private const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _snapshotRoot;
    private readonly GitWorktreeSnapshotScanner _scanner;
    private readonly ISnapshotCheckpointSink? _checkpoints;
    private readonly ISnapshotDiskSpaceProbe _diskSpace;
    private readonly ISnapshotCopyProgressSink? _copyProgress;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Initializes the exporter with a dedicated, app-owned snapshot directory.
    /// </summary>
    public VerifiedWorkspaceSnapshotExporter(
        string snapshotRoot,
        GitWorktreeSnapshotScanner scanner,
        ISnapshotCheckpointSink? checkpoints = null,
        ISnapshotDiskSpaceProbe? diskSpace = null,
        ISnapshotCopyProgressSink? copyProgress = null
    )
    {
        _snapshotRoot = Path.GetFullPath(snapshotRoot);
        _scanner = scanner;
        _checkpoints = checkpoints;
        _diskSpace = diskSpace ?? new SystemSnapshotDiskSpaceProbe();
        _copyProgress = copyProgress;
    }

    /// <inheritdoc/>
    public async Task<WorkspaceSnapshotReceipt> ExportAsync(
        string workspacePath,
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        string root = Path.GetFullPath(workspacePath);
        WorkspaceSnapshotPathPolicy.EnsureSeparated(root, _snapshotRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(root);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_snapshotRoot);

        WorktreeSnapshotScan before = await _scanner.InspectAsync(root, cancellationToken);
        SnapshotTree initial = ExpandChanges(root, before.ChangedPaths, cancellationToken);

        // Check known payload sizes before creating the app-owned staging directory.
        // Staged blobs may add more bytes, so this is a conservative early rejection,
        // not a guarantee against exhaustion during copying.
        long estimatedBytes = new FileInfo(before.IndexPath).Length;
        foreach (string relative in initial.Files)
        {
            estimatedBytes = checked(
                estimatedBytes + new FileInfo(Resolve(root, relative)).Length
            );
        }

        if (estimatedBytes > MaxSnapshotBytes)
        {
            throw new InvalidOperationException("Snapshot exceeds its configured byte budget.");
        }

        SnapshotStorageBudget.RequireSpace(_diskSpace, _snapshotRoot, estimatedBytes);

        string ownerDirectory = Path.Combine(_snapshotRoot, operationId.ToString("N"));
        Directory.CreateDirectory(ownerDirectory);
        Guid snapshotId = Guid.NewGuid();
        string staging = Path.Combine(ownerDirectory, ".pending-" + snapshotId.ToString("N"));
        string published = Path.Combine(ownerDirectory, "snapshot-" + snapshotId.ToString("N"));

        // A failed or interrupted snapshot remains visibly pending for later diagnostics.
        Directory.CreateDirectory(staging);
        string payloadDirectory = Path.Combine(staging, "files");
        Directory.CreateDirectory(payloadDirectory);

        int fileCount = 0;
        long copiedBytes = 0;

        string indexTarget = Path.Combine(staging, "index.bin");
        SnapshotStorageBudget.RequireSpace(
            _diskSpace,
            _snapshotRoot,
            new FileInfo(before.IndexPath).Length
        );
        (string indexHash, long indexLength) = await CopyVerifiedAsync(
            before.IndexPath,
            indexTarget,
            SnapshotCopyPhase.ExportIndex,
            cancellationToken
        );
        fileCount++;
        copiedBytes += indexLength;
        await CheckpointAsync(
            SnapshotCheckpoint.ExportIndexCopied,
            staging,
            cancellationToken
        );

        List<WorkspaceSnapshotFile> files = [];

        foreach (string relative in initial.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Resolve(root, relative);
            FileAttributes attributes = File.GetAttributes(path);
            EnsureNotReparse(path);
            Directory.CreateDirectory(Resolve(payloadDirectory, relative));
            files.Add(
                new WorkspaceSnapshotFile(
                    relative,
                    true,
                    null,
                    0,
                    attributes,
                    Directory.GetLastWriteTimeUtc(path)
                )
            );
        }

        foreach (string relative in initial.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Resolve(root, relative);
            string destination = Resolve(payloadDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureNotReparse(source);

            long length = new FileInfo(source).Length;

            if (fileCount >= MaxSnapshotFiles || copiedBytes + length > MaxSnapshotBytes)
            {
                throw new InvalidOperationException(
                    "Snapshot exceeds the conservative file/byte budget. "
                        + "The active worktree was not modified."
                );
            }

            SnapshotStorageBudget.RequireSpace(_diskSpace, _snapshotRoot, length);
            FileAttributes attributes = File.GetAttributes(source);
            DateTime lastWrite = File.GetLastWriteTimeUtc(source);
            (string digest, long actualLength) = await CopyVerifiedAsync(
                source,
                destination,
                SnapshotCopyPhase.ExportWorkingFile,
                cancellationToken
            );
            fileCount++;
            copiedBytes += actualLength;

            files.Add(
                new WorkspaceSnapshotFile(
                    relative,
                    false,
                    digest,
                    actualLength,
                    attributes,
                    lastWrite
                )
            );
        }

        // A staged file can differ from the current working copy; back up both versions.
        GitStagedBlobExporter stagedExporter = new();
        List<WorkspaceSnapshotFile> stagedFiles = [];
        string stagedRoot = Path.Combine(staging, "staged");
        Directory.CreateDirectory(stagedRoot);

        foreach (string relative in GitStagedBlobExporter.FindStagedFiles(before.Status))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotStorageBudget.RequireSpace(_diskSpace, _snapshotRoot, 0);

            if (fileCount >= MaxSnapshotFiles)
            {
                throw new InvalidOperationException("Snapshot exceeds its staged-file budget.");
            }

            string destination = Resolve(stagedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long length = await stagedExporter.ExportAsync(
                root,
                relative,
                destination,
                cancellationToken
            );

            copiedBytes += length;
            fileCount++;

            if (copiedBytes > MaxSnapshotBytes)
            {
                throw new InvalidOperationException("Snapshot exceeds its staged-byte budget.");
            }

            stagedFiles.Add(
                new WorkspaceSnapshotFile(
                    relative,
                    false,
                    await HashFileAsync(destination, cancellationToken),
                    length,
                    File.GetAttributes(destination),
                    File.GetLastWriteTimeUtc(destination)
                )
            );
        }

        WorkspaceSnapshotManifest manifest = new()
        {
            Version = WorkspaceSnapshotManifest.CurrentVersion,
            SnapshotId = snapshotId,
            OperationId = operationId,
            WorkspacePath = root,
            HeadCommitSha = before.Head,
            StatusBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(before.Status)),
            GitIndexSha256 = indexHash,
            Files = files,
            StagedFiles = stagedFiles,
            MissingPaths = initial.Missing,
        };

        string manifestPath = Path.Combine(staging, "manifest.json");
        await using (FileStream stream = new(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough
        ))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                manifest,
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken
            );
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await CheckpointAsync(
            SnapshotCheckpoint.ExportReadyToPublish,
            staging,
            cancellationToken
        );

        // The manifest was already written. Re-check the sources here, as close as
        // possible to publication, to reject edits during manifest serialization.
        WorktreeSnapshotScan after = await _scanner.InspectAsync(root, cancellationToken);
        SnapshotTree finalTree = ExpandChanges(root, after.ChangedPaths, cancellationToken);

        if (
            before.Head != after.Head
            || before.Status != after.Status
            || !before.IndexPath.Equals(after.IndexPath, StringComparison.OrdinalIgnoreCase)
            || !initial.Files.SequenceEqual(finalTree.Files, PathComparer)
            || !initial.Directories.SequenceEqual(finalTree.Directories, PathComparer)
            || !initial.Missing.SequenceEqual(finalTree.Missing, PathComparer)
            || !string.Equals(
                indexHash,
                await HashFileAsync(before.IndexPath, cancellationToken),
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                "The workspace changed while capturing a snapshot; publication was cancelled."
            );
        }

        // Check again after all data was copied, not only during each file's initial copy.
        foreach (WorkspaceSnapshotFile file in files.Where(file => !file.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !string.Equals(
                    file.Sha256,
                    await HashFileAsync(Resolve(root, file.RelativePath), cancellationToken),
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    "A source file changed while the snapshot was being verified."
                );
            }
        }

        Directory.Move(staging, published);
        await CheckpointAsync(
            SnapshotCheckpoint.ExportPublished,
            published,
            cancellationToken
        );

        return new WorkspaceSnapshotReceipt(
            snapshotId,
            operationId,
            published,
            fileCount,
            copiedBytes
        );
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

    private async Task<(string Hash, long Length)> CopyVerifiedAsync(
        string source,
        string destination,
        SnapshotCopyPhase phase,
        CancellationToken cancellationToken
    )
    {
        await SnapshotFileCopier.CopyAsync(
            source,
            destination,
            FileMode.CreateNew,
            phase,
            _copyProgress,
            cancellationToken
        );

        string hash = await HashFileAsync(destination, cancellationToken);
        string sourceHash = await HashFileAsync(source, cancellationToken);

        if (hash != sourceHash)
        {
            throw new InvalidOperationException(
                "Source contents changed during snapshot copy; nothing was published."
            );
        }

        return (hash, new FileInfo(destination).Length);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan
        );
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static SnapshotTree ExpandChanges(
        string root,
        IReadOnlyList<string> changePaths,
        CancellationToken cancellationToken
    )
    {
        HashSet<string> files = new(PathComparer);
        HashSet<string> directories = new(PathComparer);
        HashSet<string> missing = new(PathComparer);
        Stack<string> pending = new(changePaths);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = pending.Pop().TrimEnd('/');
            string path = Resolve(root, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);

            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                missing.Add(relative);
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                missing.Add(relative);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A changed path is a symlink or junction; snapshot refused."
                );
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                directories.Add(relative);

                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    pending.Push(
                        Path.GetRelativePath(root, child).Replace(
                            Path.DirectorySeparatorChar,
                            '/'
                        )
                    );
                }
            }
            else
            {
                files.Add(relative);
            }

            if (files.Count + directories.Count > MaxSnapshotFiles)
            {
                throw new InvalidOperationException("Snapshot contains too many changed paths.");
            }
        }

        List<string> sortedFiles = files.OrderBy(path => path, PathComparer).ToList();
        List<string> sortedDirectories = directories.OrderBy(path => path, PathComparer).ToList();
        List<string> sortedMissing = missing.OrderBy(path => path, PathComparer).ToList();

        return new SnapshotTree(sortedFiles, sortedDirectories, sortedMissing);
    }

    private static string Resolve(string root, string relative) =>
        WorkspaceSnapshotPathPolicy.Resolve(root, relative);

    private static void EnsureNotReparse(string path) =>
        WorkspaceSnapshotPathPolicy.EnsureNotReparse(path);

    private sealed record SnapshotTree(
        List<string> Files,
        List<string> Directories,
        List<string> Missing
    );
}
