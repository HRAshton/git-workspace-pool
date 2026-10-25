using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;

namespace GitWorkspacePool.Tray.Settings;

/// <summary>Explicit trust/clone consent; no network or file changes occur before confirmation.</summary>
internal sealed class RepositoryRegistrationForm : Form
{
    private readonly TextBox _url = new() { Dock = DockStyle.Fill };
    private readonly TextBox _source = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workspaces = new() { Dock = DockStyle.Fill };
    private readonly TextBox _patterns = new() { Dock = DockStyle.Fill, Multiline = true, Height = 70, Text = "main" };
    private readonly CheckBox _create = new() { AutoSize = true, Checked = true, Text = "Create a new bare clone (Git object store, no working files)" };

    public RepositoryRegistrationForm(string? cloneUrl)
    {
        Text = cloneUrl is null ? "Register Git repository" : "Repository is not registered";
        Width = 800; Height = 450; MinimumSize = new(650, 400);
        StartPosition = FormStartPosition.CenterScreen;
        _url.Text = cloneUrl ?? "";
        _url.ReadOnly = cloneUrl is not null;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitWorkspacePool");
        string identity = cloneUrl is null ? Guid.NewGuid().ToString("N") : GitBranchRequest.GetRepositoryIdentity(cloneUrl)[4..20];
        _source.Text = Path.Combine(root, "clones", identity + ".git");
        _workspaces.Text = Path.Combine(root, "workspaces", identity);
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 3, AutoScroll = true };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.Absolute, 90));
        Add(layout, "Git clone URL", _url);
        Add(layout, "Source clone", _source, true);
        Add(layout, "Workspace root", _workspaces, true);
        Add(layout, "Clone mode", _create);
        Add(layout, "Manual warm branches", _patterns);
        Label note = new() { AutoSize = true, MaximumSize = new(650, 0), Text =
            "To use an existing normal or bare clone, clear Create a new bare clone and select its folder. "
            + "SSH uses your configured agent and known_hosts; establish trust in a terminal first. "
            + "HTTPS uses your Git credential helper. No passwords are stored here. "
            + "Confirming registers this repository and allows explicit branch preparation; no background fetches run." };
        int row = layout.RowCount++;
        layout.Controls.Add(note, 0, row); layout.SetColumnSpan(note, 3);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        Button register = new() { Text = "Register repository", AutoSize = true };
        register.Click += (_, _) => Confirm();
        actions.Controls.AddRange([cancel, register]);
        Controls.Add(layout); Controls.Add(actions); AcceptButton = register; CancelButton = cancel;
    }

    private void Add(TableLayoutPanel layout, string caption, Control input, bool browse = false)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(0, 8, 0, 10) }, 0, row);
        layout.Controls.Add(input, 1, row);
        if (!browse) return;
        Button button = new() { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using FolderBrowserDialog dialog = new();
            if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
        };
        layout.Controls.Add(button, 2, row);
    }

    public GitRepositoryRegistration? Registration { get; private set; }
    public bool CreateBareClone => _create.Checked;

    private void Confirm()
    {
        try
        {
            string url = _url.Text.Trim();
            _ = GitBranchRequest.NormalizeCloneUrl(url);
            string source = _source.Text.Trim();
            string root = _workspaces.Text.Trim();
            if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Select absolute source and workspace paths.");
            new ManagedWorkspacePaths().EnsureSafe(source, root, Path.Combine(root, "registration-preflight"));
            if (CreateBareClone ? Directory.Exists(source) || File.Exists(source) : !Directory.Exists(source))
                throw new InvalidOperationException(CreateBareClone ? "Choose a new, absent clone folder. Nothing will be overwritten." : "Choose an existing normal or bare clone.");
            string[] patterns = _patterns.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patterns.Any(branch => !GitBranchRequest.IsSafeBranch(branch))) throw new InvalidOperationException("Use literal short branch names.");
            Registration = new GitRepositoryRegistration(url, source, root) { BaselineBranches = patterns };
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Repository was not registered", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
