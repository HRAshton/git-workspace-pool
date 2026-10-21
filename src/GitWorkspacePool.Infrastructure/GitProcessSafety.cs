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

        // Explicitly make Git LFS checkout an identity filter. External git-lfs
        // must not run while the application checks out an untrusted PR.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("filter.lfs.process=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("filter.lfs.smudge=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("filter.lfs.required=false");

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
    }

    /// <summary>
    /// Rejects any effective executable Git filter except LFS, which is explicitly disabled.
    /// </summary>
    /// <param name="nullDelimitedNames">Effective Git configuration key names.</param>
    public static void RejectExecutableFilters(string nullDelimitedNames)
    {
        foreach (string name in nullDelimitedNames.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries
        ))
        {
            if (!name.StartsWith("filter.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool executable = name.EndsWith(".process", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".smudge", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".clean", StringComparison.OrdinalIgnoreCase);

            // Even a globally configured LFS filter cannot run: Configure()
            // overrides its process/smudge commands with empty values.
            if (executable && !name.StartsWith("filter.lfs.", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The Git clone configures an external filter driver. "
                    + "The application refuses checkout to avoid running project-controlled code."
                );
            }
        }
    }
}
