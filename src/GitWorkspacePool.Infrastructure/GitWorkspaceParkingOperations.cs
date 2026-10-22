using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Validates physical parking paths and delegates whole-worktree moves to Git.
/// </summary>
public sealed class GitWorkspaceParkingOperations : IWorkspaceParkingOperations
{
    private readonly IWorkspaceGit _git;

    /// <summary>Initializes the worktree move adapter.</summary>
    public GitWorkspaceParkingOperations(IWorkspaceGit git)
    {
        _git = git;
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void PrepareParkingDirectory(string original, string destination)
    {
        string oldParent = Path.GetDirectoryName(Path.GetFullPath(original))
            ?? throw new InvalidOperationException("Original worktree has no parent.");
        string parkingParent = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("Parking worktree has no parent.");
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!parkingParent.Equals(Path.Combine(oldParent, ".gwp-parked"), comparison))
        {
            throw new InvalidOperationException("Parking target is outside its original root.");
        }

        CheckAncestors(oldParent);
        CheckAncestors(parkingParent);
        Directory.CreateDirectory(parkingParent);
        CheckAncestors(parkingParent);
    }

    /// <inheritdoc/>
    public void EnsureReleasePathsSafe(string repository, string original, string parked)
    {
        CheckAncestors(Path.GetFullPath(repository));
        CheckAncestors(Path.GetFullPath(original));
        CheckAncestors(Path.GetFullPath(parked));
    }

    /// <inheritdoc/>
    public Task<bool> IsRegisteredAsync(
        string repository,
        string path,
        CancellationToken cancellationToken
    ) => _git.IsRegisteredWorktreeAsync(repository, path, cancellationToken);

    /// <inheritdoc/>
    public async Task EnsureMoveSupportedAsync(
        string worktree,
        CancellationToken cancellationToken
    )
    {
        // Refuse junctions/symlinks on the inspected path before writing any
        // parking intent, not only inside the later Git move adapter.
        CheckAncestors(Path.GetFullPath(worktree));
        RefuseNestedGitRepositories(worktree, cancellationToken);

        ProcessStartInfo start = new("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        GitProcessSafety.Configure(start);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(worktree);
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--stage");
        start.ArgumentList.Add("-z");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not inspect Git submodules.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string entries = await outputTask;
            string stderr = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git submodule inspection failed: {stderr.Trim()}"
                );
            }

            foreach (string entry in entries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.StartsWith("160000 ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Parking a worktree with Git submodule entries is unsupported. "
                            + "No move intent was recorded and no files were changed."
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public Task<string> GetHeadAsync(string path, CancellationToken cancellationToken)
        => _git.GetWorktreeCommitAsync(path, cancellationToken);

    /// <inheritdoc/>
    public async Task MoveAsync(
        string repository,
        string source,
        string destination,
        CancellationToken cancellationToken
    )
    {
        CheckAncestors(Path.GetFullPath(source));
        RefuseNestedGitRepositories(source, cancellationToken);
        CheckAncestors(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (Exists(destination))
        {
            throw new InvalidOperationException("Parking destination is already occupied.");
        }

        // Git updates linked-worktree administrative metadata; Directory.Move would not.
        // Git refuses unsupported moves such as linked worktrees containing submodules.
        await GitCliWorkspaceGit.MoveWorktreeAsync(
            repository,
            source,
            destination,
            cancellationToken
        );
    }

    private static void RefuseNestedGitRepositories(
        string worktree,
        CancellationToken cancellationToken
    )
    {
        // Only the linked worktree root may have its own .git metadata.
        // A nested .git directory or gitfile belongs to another repository.
        // Do not follow reparse points, including junctions outside the root.
        const int maxEntries = 100000;
        string root = Path.GetFullPath(worktree);
        Stack<string> directories = new();
        directories.Push(root);
        int inspected = 0;

        while (directories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > maxEntries)
                {
                    throw new InvalidOperationException(
                        "Parking scan exceeded its safety limit; no worktree move was attempted."
                    );
                }

                string name = Path.GetFileName(path);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    if (!directory.Equals(
                        root,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal
                    ))
                    {
                        throw new InvalidOperationException(
                            "Parking cannot move an untracked nested Git repository; "
                                + "its files remain at the original path."
                        );
                    }

                    continue;
                }

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A link can point outside the worktree. Skip, never traverse.
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(path);
                }
            }
        }
    }

    private static void CheckAncestors(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Parking cannot follow a symlink or junction."
                    );
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = parent;
        }
    }
}
