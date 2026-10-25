using System.Diagnostics;
using System.Drawing;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Transport;
using GitWorkspacePool.Tray.Infrastructure;
using GitWorkspacePool.Tray.Settings;

namespace GitWorkspacePool.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NotifyIcon _icon;
    private readonly SqliteWarmWorkspaceRegistry _registry;
    private readonly WarmWorkspacePool _pool;
    private readonly OpenGitBranchHandler _open;
    private readonly Task _listener;

    public TrayContext(string? initialRequest)
    {
        _ = _dispatcher.Handle;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitWorkspacePool", "warm-v1");
        _registry = new(Path.Combine(root, "workspaces.db"));
        _pool = new(_registry, new WarmWorktreeGit(), new FileWorkspaceOperationLock(Path.Combine(root, "locks")));
        _open = new(new SettingsStore(), _pool, new VsCodeLauncher());
        ContextMenuStrip menu = new();
        menu.Items.Add("Open branch...", null, async (_, _) => await RunAsync(async token =>
        {
            GitBranchRequest? request = AskForBranch();
            if (request is not null) await _open.HandleAsync(request, token);
        }));
        menu.Items.Add("Prepare warm workspace...", null, async (_, _) => await RunAsync(async token =>
        {
            GitBranchRequest? request = AskForBranch();
            if (request is null) return;
            GitRepositoryRegistration repository = new SettingsStore().Find(request)
                ?? throw new InvalidOperationException("Register the repository in Settings first.");
            WarmWorkspace workspace = await _pool.PrepareAsync(repository, request.Branch, token);
            Notify("Warm workspace prepared", workspace.Path + "\nPrepare dependencies manually here, then keep tracked files clean.");
        }));
        ToolStripMenuItem workspaces = new("List workspaces / release...");
        workspaces.DropDownOpening += (_, _) => Populate(workspaces);
        menu.Items.Add(workspaces);
        menu.Items.Add("Diagnostics (read-only)...", null, (_, _) => Diagnostics());
        menu.Items.Add("Settings...", null, (_, _) =>
        {
            using WorkspaceSettingsForm form = new(); form.ShowDialog();
        });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new() { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Git Workspace Pool", ContextMenuStrip = menu, Visible = true };
        _listener = new NamedPipeRequestServer(PipeRelay.PipeName).ListenAsync(raw =>
            _dispatcher.BeginInvoke((Action)(async () => await OpenProtocolAsync(raw))), _shutdown.Token);
        if (initialRequest is not null)
            _dispatcher.BeginInvoke((Action)(async () => await OpenProtocolAsync(initialRequest)));
    }

    private Task OpenProtocolAsync(string raw) => RunAsync(async token =>
    {
        if (!GitBranchProtocol.TryParse(raw, out GitBranchRequest? request)) throw new InvalidOperationException("Expected a version 2 prworkspace link.");
        await _open.HandleAsync(request, token);
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(SettingsStore.Read().OperationTimeoutMinutes));
            await action(timeout.Token);
        }
        catch (OperationCanceledException) { if (!_shutdown.IsCancellationRequested) Notify("Operation stopped", "Any interrupted transition is retained for manual recovery."); }
        catch (Exception exception) { Notify("Workspace operation failed", exception.Message); }
    }

    private static GitBranchRequest? AskForBranch()
    {
        using Form form = new() { Text = "Open / prepare Git branch", Width = 650, Height = 210, StartPosition = FormStartPosition.CenterScreen };
        ComboBox url = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        url.Items.AddRange(SettingsStore.Read().GitRepositories.Select(repository => (object)repository.CloneUrl).ToArray());
        if (url.Items.Count > 0) url.SelectedIndex = 0;
        ComboBox branch = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown };
        void LoadBranches()
        {
            branch.Items.Clear();
            GitRepositoryRegistration? repository = SettingsStore.Read().GitRepositories.FirstOrDefault(row => row.CloneUrl == url.Text);
            if (repository is not null) branch.Items.AddRange(repository.BaselineBranches.Cast<object>().ToArray());
        }
        url.SelectedIndexChanged += (_, _) => LoadBranches();
        LoadBranches();
        Button open = new() { Text = "Continue", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        form.Controls.Add(branch); form.Controls.Add(new Label { Text = "Branch", Dock = DockStyle.Top });
        form.Controls.Add(url); form.Controls.Add(new Label { Text = "Registered repository", Dock = DockStyle.Top }); form.Controls.Add(open);
        form.AcceptButton = open;
        if (form.ShowDialog() != DialogResult.OK) return null;
        GitBranchRequest request = new(url.Text, branch.Text.Trim());
        return request.IsValid ? request : throw new InvalidOperationException("Choose a registered URL and valid short branch.");
    }

    private void Populate(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        try
        {
            foreach (GitRepositoryRegistration repository in SettingsStore.Read().GitRepositories)
            foreach (WarmWorkspace row in _registry.List(repository.RepositoryIdentity))
            {
                ToolStripMenuItem item = new($"{row.State}: {row.Branch ?? row.Head[..Math.Min(12, row.Head.Length)]} ({row.Path})");
                item.DropDownItems.Add("Open folder", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { row.Path }, UseShellExecute = false }));
                if (row.State == WarmWorkspaceState.Assigned)
                    item.DropDownItems.Add("Release if eligible", null, async (_, _) => await RunAsync(token => _pool.ReleaseAsync(repository, row.Id, token)));
                menu.DropDownItems.Add(item);
            }
            if (menu.DropDownItems.Count == 0) menu.DropDownItems.Add("No managed workspaces.");
        }
        catch (Exception exception) { menu.DropDownItems.Add(exception.Message); }
    }

    private void Diagnostics()
    {
        try
        {
            string[] lines = SettingsStore.Read().GitRepositories.SelectMany(repository => _registry.List(repository.RepositoryIdentity))
                .Select(row => $"{(row.State == WarmWorkspaceState.Transitioning ? "Interrupted / manual recovery" : row.State.ToString())}: {row.Path}\nRecorded HEAD: {row.Head}").ToArray();
            MessageBox.Show(string.Join("\n\n", lines.DefaultIfEmpty("No workspaces. Legacy state is never opened or imported.")), "Read-only ownership diagnostics");
        }
        catch (Exception exception) { Notify("Diagnostics failed", exception.Message); }
    }

    private void Notify(string title, string message) => _icon.ShowBalloonTip(10000, title, message, ToolTipIcon.Info);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();
            _icon.Visible = false; _icon.Dispose(); _dispatcher.Dispose();
            // Do not dispose the token source while outstanding operations still use it.
            _ = _listener.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }
}
