using System.Diagnostics;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class VsCodeLauncher : IEditorLauncher
{
    public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? configured = SettingsStore.Read().EditorExecutable;
        string executable = configured ?? string.Empty;

        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Microsoft VS Code",
                "Code.exe"
            );
        }

        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                "VS Code not found. Set editorExecutable to the absolute path of Code.exe in settings.json."
            );
        }

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(workspacePath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch VS Code.");

        return Task.CompletedTask;
    }
}
