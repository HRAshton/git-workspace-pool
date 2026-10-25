using System.Text.Json;
using System.Text.Json.Serialization;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class AppSettings
{
    public List<GitRepositoryRegistration> GitRepositories { get; set; } = [];

    public SnapshotExportLimits SnapshotLimits { get; set; } = new();

    public string? EditorExecutable { get; set; } = "code.exe";

    public int OperationTimeoutMinutes { get; set; } = 15;

    public bool EnableLocalTimings { get; set; }

    public bool OfferVerifiedBranchReuse { get; set; }

    public int BaselineRefreshMinutes { get; set; } = 30;

    public WorkspaceStorageWarningLimits StorageWarningLimits { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; set; }
}
