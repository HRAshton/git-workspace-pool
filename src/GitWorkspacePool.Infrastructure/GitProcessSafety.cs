using System.Diagnostics;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Disables implicit external Git hook and FSMonitor execution in application Git CLI calls.
/// </summary>
internal static class GitProcessSafety
{
    /// <summary>
    /// Applies restricted Git command-line configuration and non-interactive environment.
    /// </summary>
    /// <param name="startInfo">A new Git process, before subcommand arguments are appended.</param>
    public static void Configure(ProcessStartInfo startInfo)
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        string emptyHooks = Path.Combine(appData, "GitWorkspacePool", "empty-git-hooks");
        Directory.CreateDirectory(emptyHooks);

        // -c overrides repository-local core.hooksPath and core.fsmonitor settings.
        // This prevents Git's post-checkout hook and externally configured FSMonitor
        // from executing simply because the user clicked a PR browser button.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.hooksPath=" + emptyHooks);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
    }
}
