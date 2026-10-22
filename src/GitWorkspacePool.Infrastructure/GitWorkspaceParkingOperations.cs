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
    public Task<bool> IsRegisteredAsync(
        string repository,
        string path,
        CancellationToken cancellationToken
    ) => _git.IsRegisteredWorktreeAsync(repository, path, cancellationToken);

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
