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
        startInfo.Environment.Remove("SSH_ASKPASS");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
    }

    /// <summary>
    /// Rejects effective Git configuration that can redirect a trusted
    /// remote, choose external transport commands or launch untrusted
    /// authentication helpers. Input uses git config --list --null records.
    /// </summary>
    public static void RejectUnsafeFetchConfiguration(string nullDelimitedConfiguration)
    {
        foreach (string record in nullDelimitedConfiguration.Split(
            '\0', StringSplitOptions.RemoveEmptyEntries
        ))
        {
            int separator = record.IndexOf('\n');
            if (separator <= 0)
            {
                throw new InvalidOperationException(
                    "Cannot validate malformed effective Git configuration."
                );
            }

            string key = record[..separator];
            string value = record[(separator + 1)..];

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

            bool helper = key.Equals("credential.helper", StringComparison.OrdinalIgnoreCase)
                || (key.StartsWith("credential.", StringComparison.OrdinalIgnoreCase)
                    && key.EndsWith(".helper", StringComparison.OrdinalIgnoreCase));
            bool askPass = key.Equals("core.askpass", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value);

            // Empty helper entries reset the list in Git; bare helper names
            // (e.g. manager or cache) remain supported. Shell snippets,
            // executable paths and command lines are not auto-trusted.
            bool unsafeHelper = helper
                && value.Length > 0
                && !value.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                );

            if (askPass || unsafeHelper)
            {
                throw new InvalidOperationException(
                    "The Git clone configures an untrusted credential helper or askpass command. "
                    + "Use a simple named credential helper or remove the override before opening a PR."
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
