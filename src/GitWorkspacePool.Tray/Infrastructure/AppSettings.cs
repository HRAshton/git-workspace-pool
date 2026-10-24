using System.Text.Json;
using System.Text.Json.Serialization;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class AppSettings
{
    public List<RepositoryRegistration> Repositories { get; set; } = [];

    public List<GitRepositoryRegistration> GitRepositories { get; set; } = [];

    public SnapshotExportLimits SnapshotLimits { get; set; } = new();

    public string? EditorExecutable { get; set; }

    public int OperationTimeoutMinutes { get; set; } = 15;

    public bool EnableLocalTimings { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; set; }
}
