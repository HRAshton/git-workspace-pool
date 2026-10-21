using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class AppSettings
{
    public List<RepositoryRegistration> Repositories { get; set; } = [];

    public string? EditorExecutable { get; set; }
}
