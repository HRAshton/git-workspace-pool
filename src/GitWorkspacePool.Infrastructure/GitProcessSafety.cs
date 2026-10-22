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

        // A protocol launcher must not inherit shell-command overrides from
        // the browser/parent process. Standard SSH agent auth still works.
        startInfo.Environment.Remove("GIT_SSH_COMMAND");
        startInfo.Environment.Remove("GIT_SSH");
        startInfo.Environment.Remove("GIT_PROXY_COMMAND");
        startInfo.Environment.Remove("GIT_ASKPASS");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
    }

    /// <summary>
    /// Refuses Git configuration that can redirect a trusted origin or select
    /// an arbitrary external command during fetch. Effective key names must
    /// include global, local and conditional Git configuration.
    /// </summary>
    public static void RejectUnsafeFetchConfiguration(string nullDelimitedNames)
    {
        foreach (string key in nullDelimitedNames.Split(
            '\0', StringSplitOptions.RemoveEmptyEntries
        ))
        {
            bool urlRewrite = key.StartsWith("url.", StringComparison.OrdinalIgnoreCase)
                && (key.EndsWith(".insteadof", StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith(".pushinsteadof", StringComparison.OrdinalIgnoreCase));
            bool externalTransport = key.Equals("core.sshcommand", StringComparison.OrdinalIgnoreCase)
                || key.Equals("core.gitproxy", StringComparison.OrdinalIgnoreCase)
                || key.Equals("remote.origin.vcs", StringComparison.OrdinalIgnoreCase)
                || key.Equals("remote.origin.uploadpack", StringComparison.OrdinalIgnoreCase)
                || key.Equals("remote.origin.receivepack", StringComparison.OrdinalIgnoreCase);

            if (urlRewrite || externalTransport)
            {
                throw new InvalidOperationException(
                    "The Git clone configures an unsafe URL rewrite or external Git transport command. "
                    + "Remove the override before opening a PR; no fetch was performed."
                );
            }
        }
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
