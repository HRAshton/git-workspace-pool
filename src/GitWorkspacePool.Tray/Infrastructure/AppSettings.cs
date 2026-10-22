using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class AppSettings
{
    public List<RepositoryRegistration> Repositories { get; set; } = [];

    public SnapshotExportLimits SnapshotLimits { get; set; } = new();

    public string? EditorExecutable { get; set; }
}
