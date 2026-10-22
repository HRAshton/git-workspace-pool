using System.Diagnostics;
using System.Security.Cryptography;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Fail-closed, copy-only npm dependency reuse. A cache is transferable only
/// when both package files match byte-for-byte and Git ignores node_modules
/// in both worktrees. Symlinks, tracked paths and collisions are never copied.
/// </summary>
public sealed class VerifiedNpmArtifactCopier : IWorkspaceWarmArtifactCopier
{
    private const int MaxFiles = 20000;
    private const long MaxBytes = 512L * 1024 * 1024;
    private const int MaxDirectories = 30000;
    private const long MaxManifestBytes = 4L * 1024 * 1024;

    /// <inheritdoc/>
    public async Task<WarmArtifactTransferReceipt> CopyAsync(
        WorkspaceSession releasedSource,
        WorkspaceSession activeDestination,
        CancellationToken cancellationToken
    )
    {
        string donorRoot = Path.GetFullPath(
            releasedSource.ParkingPath
                ?? throw new InvalidOperationException("The donor is not parked.")
        );
        string recipientRoot = Path.GetFullPath(activeDestination.WorkspacePath);
        WorkspaceSnapshotPathPolicy.EnsureSeparated(donorRoot, recipientRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(donorRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(recipientRoot);

        // An npm cache depends on both the lockfile and package metadata.
        foreach (string name in new[] { "package.json", "package-lock.json" })
        {
            string a = WorkspaceSnapshotPathPolicy.Resolve(donorRoot, name);
            string b = WorkspaceSnapshotPathPolicy.Resolve(recipientRoot, name);
            if (!File.Exists(a) || !File.Exists(b))
            {
                throw new InvalidOperationException(
                    "Both PR worktrees require package.json and package-lock.json."
                );
            }

            WorkspaceSnapshotPathPolicy.EnsureNotReparse(a);
            WorkspaceSnapshotPathPolicy.EnsureNotReparse(b);
            if (new FileInfo(a).Length > MaxManifestBytes
                || new FileInfo(b).Length > MaxManifestBytes
                || !string.Equals(
                    await HashAsync(a, cancellationToken),
                    await HashAsync(b, cancellationToken),
                    StringComparison.Ordinal
                ))
            {
                throw new InvalidOperationException(
                    "npm dependency manifests differ or exceed the safe comparison budget."
                );
            }
        }

        string source = WorkspaceSnapshotPathPolicy.Resolve(donorRoot, "node_modules");
        string dest = WorkspaceSnapshotPathPolicy.Resolve(recipientRoot, "node_modules");
        if (!Directory.Exists(source)
            || Directory.Exists(dest)
            || File.Exists(dest))
        {
            throw new InvalidOperationException(
                "A donor node_modules directory is required and the destination must not exist."
            );
        }

        WorkspaceSnapshotPathPolicy.EnsureNotReparse(source);
        // Git's own ignore rules must consent to keeping every imported cache
        // file outside the versioned index; no external process executes code.
        await VerifyIgnoredAndUntrackedAsync(donorRoot, cancellationToken);
        await VerifyIgnoredAndUntrackedAsync(recipientRoot, cancellationToken);

        List<(string Relative, long Length, string Hash)> files = [];
        Stack<string> directories = new();
        directories.Push(source);
        int directoriesSeen = 0;
        long totalBytes = 0;
        while (directories.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = directories.Pop();
            WorkspaceSnapshotPathPolicy.EnsureNotReparse(directory);
            if (++directoriesSeen > MaxDirectories)
            {
                throw new InvalidOperationException("npm cache directory budget exceeded.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "npm cache contains a link or junction; no files were imported."
                    );
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0)
                {
                    throw new InvalidOperationException("Unsupported npm cache file type.");
                }

                string relative = WorkspaceSnapshotPathPolicy.NormalizeRelative(
                    Path.GetRelativePath(source, entry)
                );
                long length = new FileInfo(entry).Length;
                totalBytes = checked(totalBytes + length);
                if (files.Count >= MaxFiles || totalBytes > MaxBytes)
                {
                    throw new InvalidOperationException("npm cache transfer exceeds its safe budget.");
                }

                files.Add((relative, length, await HashAsync(entry, cancellationToken)));
            }
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Relative, b.Relative));
        if (files.Count == 0)
        {
            throw new InvalidOperationException("An empty npm dependency cache is not transferable.");
        }

        Stopwatch clock = Stopwatch.StartNew();
        // All eligibility checks happen before creating the destination.
        // A failed copy is kept as evidence; no rollback removes unknown data.
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(dest);
        Directory.CreateDirectory(dest);
        long copiedBytes = 0;
        foreach ((string relative, long length, string hash) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string input = WorkspaceSnapshotPathPolicy.Resolve(source, relative);
            string output = WorkspaceSnapshotPathPolicy.Resolve(dest, relative);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(input);
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(output);
            if (!File.Exists(input) || new FileInfo(input).Length != length
                || !string.Equals(await HashAsync(input, cancellationToken), hash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The source cache changed during transfer; partial copy was retained."
                );
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using FileStream inputStream = new(
                input, FileMode.Open, FileAccess.Read, FileShare.Read
            );
            await using FileStream outputStream = new(
                output, FileMode.CreateNew, FileAccess.Write, FileShare.None
            );
            byte[] buffer = new byte[64 * 1024];
            long written = 0;
            int count;
            while ((count = await inputStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written = checked(written + count);
                if (written > length || checked(copiedBytes + written) > MaxBytes)
                {
                    throw new InvalidOperationException("The source cache grew while copying.");
                }

                await outputStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            await outputStream.FlushAsync(cancellationToken);
            if (written != length)
            {
                throw new InvalidOperationException("The source cache shrank during copying.");
            }

            await outputStream.DisposeAsync();
            if (!string.Equals(
                await HashAsync(output, cancellationToken), hash, StringComparison.Ordinal
            ))
            {
                throw new InvalidOperationException(
                    "The copied artifact failed integrity verification; retained for inspection."
                );
            }

            copiedBytes = checked(copiedBytes + written);
        }

        clock.Stop();
        return new WarmArtifactTransferReceipt(
            files.Count, copiedBytes, clock.Elapsed, "node_modules"
        );
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read
        );
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    private static async Task VerifyIgnoredAndUntrackedAsync(
        string worktree,
        CancellationToken token
    )
    {
        using (Process git = StartGit(worktree, "check-ignore",
            "--no-index", "--quiet", "--", "node_modules/.gwp-probe"))
        {
            await git.WaitForExitAsync(token);
            if (git.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "node_modules must be explicitly ignored by Git in both PRs."
                );
            }
        }

        // Return the first tracked file, if any, without materializing a
        // potentially huge ls-files output or letting a large pipe deadlock.
        using Process listed = StartGit(worktree, "ls-files", "--", "node_modules");
        char[] first = new char[1];
        int read = await listed.StandardOutput.ReadAsync(first.AsMemory(), token);
        if (read != 0)
        {
            if (!listed.HasExited)
            {
                listed.Kill(entireProcessTree: true);
            }

            await listed.WaitForExitAsync(token);
            throw new InvalidOperationException(
                "node_modules contains Git-tracked paths; artifacts cannot be imported."
            );
        }

        await listed.WaitForExitAsync(token);
        if (listed.ExitCode != 0)
        {
            throw new InvalidOperationException("Git could not verify tracked npm paths.");
        }
    }

    private static Process StartGit(string directory, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("Git verification process did not start.");
    }
}
