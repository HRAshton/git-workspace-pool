using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class SettingsStore : IRepositoryCatalog, IGitBranchCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitWorkspacePool",
        "settings.json"
    );

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        if (!File.Exists(FilePath))
        {
            AppSettings example = new()
            {
                Repositories =
                [
                    new RepositoryRegistration(
                        "example/repo",
                        @"C:\src\repo",
                        @"C:\src\pr-workspaces"
                    ),
                ],
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(example, JsonOptions));
        }
    }

    public RepositoryRegistration? Find(PullRequestReference request)
    {
        AppSettings settings = Read();
        RepositoryRegistration[] matches = settings
            .Repositories.Where(repository =>
                repository.RepositoryKey.Equals(
                    request.RepositoryKey,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();

        if (matches.Length > 1)
        {
            throw new InvalidOperationException("Duplicate repository registration.");
        }

        RepositoryRegistration? registration = matches.SingleOrDefault();

        if (registration is not null)
        {
            if (
                !Path.IsPathFullyQualified(registration.LocalPath)
                || !Path.IsPathFullyQualified(registration.WorkspaceRoot)
                || !Directory.Exists(registration.LocalPath)
            )
            {
                throw new InvalidOperationException(
                    "The registered repository paths must be absolute, and the local clone must exist."
                );
            }

            string repositoryPath = Path.GetFullPath(registration.LocalPath);
            string workspaceRoot = Path.GetFullPath(registration.WorkspaceRoot);

            if (
                workspaceRoot.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase)
                || workspaceRoot.StartsWith(
                    repositoryPath.TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidOperationException(
                    "The managed workspace root cannot be inside the source clone."
                );
            }
        }

        return registration;
    }

    public GitRepositoryRegistration? Find(GitBranchRequest request)
    {
        GitRepositoryRegistration[] matches = Read().GitRepositories
            .Where(repository => GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Duplicate Git clone URL registration.");
        return matches.SingleOrDefault();
    }

    public static AppSettings Read()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(FilePath),
            JsonOptions
        );

        if (settings is null || settings.SnapshotLimits is null)
        {
            throw new InvalidOperationException("Invalid settings.json or SnapshotLimits.");
        }

        settings.SnapshotLimits.Validate();
        if (settings.GitRepositories is null) throw new InvalidOperationException("Invalid GitRepositories settings.");
        foreach (GitRepositoryRegistration repository in settings.GitRepositories)
        {
            _ = GitBranchRequest.NormalizeCloneUrl(repository.CloneUrl);
            if (!Path.IsPathFullyQualified(repository.LocalPath) || !Path.IsPathFullyQualified(repository.WorkspaceRoot)
                || !Directory.Exists(repository.LocalPath))
                throw new InvalidOperationException("Git repository paths must be absolute and the clone must exist.");
        }
        return settings;
    }
}

