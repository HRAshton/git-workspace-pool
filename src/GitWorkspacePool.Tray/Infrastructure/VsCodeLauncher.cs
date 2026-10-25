using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Infrastructure;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class VsCodeLauncher : IEditorLauncher
{
    public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string executable = EditorExecutableResolver.Resolve(SettingsStore.Read().EditorExecutable);
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workspacePath,
        };
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(workspacePath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch VS Code.");

        return Task.CompletedTask;
    }
}
