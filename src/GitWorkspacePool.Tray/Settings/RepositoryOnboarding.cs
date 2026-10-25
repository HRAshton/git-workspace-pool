using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray.Settings;

/// <summary>Explicit first-link trust decision, observable clone and atomic registration.</summary>
internal static class RepositoryOnboarding
{
    public static async Task RegisterAsync(
        GitRepositoryRegistration repository, bool createBareClone, CancellationToken cancellationToken)
    {
        // The link provides only URL + branch; no filesystem or executable path is taken from it.
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using GitCloneProgressForm dialog = new();
        dialog.CancelRequested += (_, _) => stop.Cancel();
        dialog.Show();
        try
        {
            if (createBareClone)
            {
                dialog.Stage("Cloning Git objects. This may take a while for large repositories...");
                await new GitCliWorkspaceGit().CloneBareAsync(
                    repository.CloneUrl, repository.LocalPath, stop.Token, new Progress<string>(dialog.Report));
            }
            dialog.Stage("Verifying source origin and registration paths...");
            await new WarmWorktreeGit().VerifySourceAsync(repository, stop.Token);
            stop.Token.ThrowIfCancellationRequested();

            // Read at publication time so simultaneous first links cannot overwrite settings.
            var snapshot = SettingsStore.ReadForEdit();
            if (snapshot.Settings.GitRepositories.Any(existing =>
                GitBranchRequest.OriginMatches(existing.CloneUrl, repository.CloneUrl)))
                throw new InvalidOperationException("Repository already registered. Reopen the link.");
            snapshot.Settings.GitRepositories.Add(repository);
            SettingsStore.Save(snapshot.Settings, snapshot.Fingerprint);
        }
        finally
        {
            dialog.Finish();
            dialog.Close();
        }
    }
}
