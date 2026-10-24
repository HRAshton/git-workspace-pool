using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Configuration;

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
            AppSettings example = new();
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
        GitRepositoryRegistration? match = matches.SingleOrDefault();
        if (match is not null) ValidatePaths(match.LocalPath, match.WorkspaceRoot);
        return match;
    }

    public static AppSettings Read()
    {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
            new AtomicConfigurationFile(FilePath).Read().Text,
            JsonOptions
        );

        if (settings is null || settings.SnapshotLimits is null)
        {
            throw new InvalidOperationException("Invalid settings.json or SnapshotLimits.");
        }

        ValidateLimits(settings);
        settings.SnapshotLimits.Validate();
        if (settings.GitRepositories is null) throw new InvalidOperationException("Invalid GitRepositories settings.");
        foreach (GitRepositoryRegistration repository in settings.GitRepositories)
        {
            _ = GitBranchRequest.NormalizeCloneUrl(repository.CloneUrl);
        }
        return settings;
    }
    public static (AppSettings Settings, string Fingerprint) ReadForEdit()
    {
        ConfigurationSnapshot snapshot = new AtomicConfigurationFile(FilePath).Read();
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(snapshot.Text, JsonOptions)
            ?? throw new InvalidOperationException("Settings must contain a JSON object.");
        return (settings, snapshot.Fingerprint);
    }

    public static string Save(AppSettings settings, string expectedFingerprint)
    {
        ValidateLimits(settings);
        settings.SnapshotLimits.Validate();
        HashSet<string> urls = new(StringComparer.Ordinal);
        foreach (GitRepositoryRegistration repository in settings.GitRepositories)
        {
            string url = GitBranchRequest.NormalizeCloneUrl(repository.CloneUrl);
            if (!urls.Add(url)) throw new InvalidOperationException("Each Git clone URL must be registered once.");
            ValidatePaths(repository.LocalPath, repository.WorkspaceRoot);
        }
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (RepositoryRegistration repository in settings.Repositories)
        {
            if (!PullRequestReference.TryParse("https://github.com/" + repository.RepositoryKey + "/pull/1", out _)
                || !keys.Add(repository.RepositoryKey))
            {
                throw new InvalidOperationException("GitHub registrations need a unique owner/repository key.");
            }
            ValidatePaths(repository.LocalPath, repository.WorkspaceRoot);
        }
        if (!string.IsNullOrWhiteSpace(settings.EditorExecutable)
            && (!Path.IsPathFullyQualified(settings.EditorExecutable) || !File.Exists(settings.EditorExecutable)))
        {
            throw new InvalidOperationException("Choose the existing absolute path of VS Code Code.exe.");
        }
        return new AtomicConfigurationFile(FilePath).Save(
            JsonSerializer.Serialize(settings, JsonOptions), expectedFingerprint).Fingerprint;
    }

    private static void ValidateLimits(AppSettings settings)
    {
        if (settings.Repositories is null || settings.GitRepositories is null || settings.SnapshotLimits is null)
            throw new InvalidOperationException("Repository lists and snapshot limits cannot be null.");
        if (settings.StorageWarningLimits is null) throw new InvalidOperationException("Storage warning settings cannot be null.");
        settings.StorageWarningLimits.Validate();
        RefreshConfiguredBaselinesService.Validate(settings.GitRepositories, settings.BaselineRefreshMinutes);
        if (settings.OperationTimeoutMinutes is < 1 or > 120)
            throw new InvalidOperationException("Operation timeout must be between 1 and 120 minutes.");
    }

    private static void ValidatePaths(string source, string root)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(root) || !Directory.Exists(source))
            throw new InvalidOperationException("Use absolute paths and an existing local clone.");
        new WorkspaceReadySlotSafety().EnsureSafe(source, root, Path.Combine(root, "settings-preflight"));
    }

}

