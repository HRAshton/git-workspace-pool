using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class WorkspaceDirectories : IWorkspaceDirectories
{
    public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    public void EnsureRoot(string root) => Directory.CreateDirectory(root);
}
