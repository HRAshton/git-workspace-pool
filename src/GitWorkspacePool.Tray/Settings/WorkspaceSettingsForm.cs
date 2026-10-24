using System.Drawing;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;
using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray.Settings;

internal sealed class WorkspaceSettingsForm : Form
{
    private readonly DataGridView _git = RepositoryGrid("HTTPS clone URL");
    private readonly DataGridView _github = RepositoryGrid("GitHub owner/repository");
    private readonly TextBox _editor = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 120, Width = 100 };
    private readonly NumericUpDown _snapshotBytes = new() { Minimum = 1, Maximum = 2147483648m, Width = 150, ThousandsSeparator = true };
    private readonly NumericUpDown _snapshotEntries = new() { Minimum = 1, Maximum = 50000, Width = 120 };
    private readonly CheckBox _timings = new() { AutoSize = true, Text = "Record local operation timings (no URLs, paths, branches or error text)" };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _lifetimeDisposed;
    private AppSettings _original;
    private string _fingerprint;

    public WorkspaceSettingsForm()
    {
        (_original, _fingerprint) = SettingsStore.ReadForEdit();
        Text = "Git Workspace Pool settings";
        Width = 1000;
        Height = 650;
        MinimumSize = new Size(750, 500);
        StartPosition = FormStartPosition.CenterScreen;
        TabControl tabs = new() { Dock = DockStyle.Fill };
        tabs.TabPages.Add(RepositoryTab("Git repositories", _git, true));
        tabs.TabPages.Add(RepositoryTab("GitHub PR repositories", _github, false));
        tabs.TabPages.Add(OptionsTab());
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        Button close = new() { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        Button save = new() { Text = "Save", AutoSize = true };
        save.Click += (_, _) => Save();
        Button reload = new() { Text = "Reload", AutoSize = true };
        reload.Click += (_, _) => Reload();
        buttons.Controls.AddRange([close, save, reload]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = close;
        LoadValues();
    }

    private static DataGridView RepositoryGrid(string identityHeader)
    {
        DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true, AllowUserToDeleteRows = true,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            RowHeadersWidth = 30, AccessibleName = identityHeader + " registrations",
        };
        grid.Columns.Add("Identity", identityHeader);
        grid.Columns.Add("Source", "Existing local clone folder");
        grid.Columns.Add("Root", "Separate workspace root");
        return grid;
    }

    private TabPage RepositoryTab(string title, DataGridView grid, bool generic)
    {
        TabPage tab = new(title);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Top, Height = 68, AutoScroll = true };
        Label help = new()
        {
            Text = generic
                ? "One row per trusted clone. Authentication uses Git Credential Manager; never enter passwords here."
                : "Optional existing GitHub PR workflow. Remove any unused example row before saving.",
            AutoSize = true, Padding = new Padding(4, 8, 4, 0),
        };
        actions.Controls.Add(help);
        Button browse = new() { Text = "Browse selected folder...", AutoSize = true };
        browse.Click += (_, _) => BrowseFolder(grid);
        Button test = new() { Text = "Check selected origin", AutoSize = true };
        test.Click += async (_, _) => await TestOriginAsync(grid, generic, test);
        actions.Controls.AddRange([browse, test]);
        tab.Controls.Add(grid);
        tab.Controls.Add(actions);
        return tab;
    }

    private TabPage OptionsTab()
    {
        TabPage tab = new("Editor and limits");
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16), ColumnCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        Button editorBrowse = new() { Text = "Browse...", AutoSize = true };
        editorBrowse.Click += (_, _) =>
        {
            using OpenFileDialog picker = new() { Filter = "VS Code executable|Code.exe|Executables|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) _editor.Text = picker.FileName;
        };
        AddOption(layout, "VS Code executable", _editor, editorBrowse);
        AddOption(layout, "Operation timeout (minutes)", _timeout);
        AddOption(layout, "Snapshot byte limit", _snapshotBytes);
        AddOption(layout, "Snapshot entry limit", _snapshotEntries);
        AddOption(layout, "Local diagnostics", _timings);
        Label notes = new()
        {
            AutoSize = true, MaximumSize = new Size(700, 0), Padding = new Padding(0, 14, 0, 0),
            Text = "Blank editor path uses the current user's standard VS Code installation. "
                + "Cancel or timeout preserves unfinished ownership and recovery evidence. "
                + "Existing worktrees are never moved by changing settings. "
                + "Snapshot limit changes apply after restarting the tray. Local timing samples exclude browser prompts and visible-editor startup.",
        };
        layout.Controls.Add(notes, 0, layout.RowCount);
        layout.SetColumnSpan(notes, 3);
        tab.Controls.Add(layout);
        return tab;
    }

    private static void AddOption(TableLayoutPanel layout, string caption, Control control, Control? extra = null)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(0, 8, 0, 12) }, 0, row);
        control.Margin = new Padding(3, 5, 3, 5);
        layout.Controls.Add(control, 1, row);
        if (extra is not null) layout.Controls.Add(extra, 2, row);
    }

    private void LoadValues()
    {
        _git.Rows.Clear();
        _github.Rows.Clear();
        foreach (GitRepositoryRegistration repository in _original.GitRepositories)
        {
            int index = _git.Rows.Add(repository.CloneUrl, repository.LocalPath, repository.WorkspaceRoot);
            _git.Rows[index].Tag = repository;
        }
        foreach (RepositoryRegistration repository in _original.Repositories)
        {
            int index = _github.Rows.Add(repository.RepositoryKey, repository.LocalPath, repository.WorkspaceRoot);
            _github.Rows[index].Tag = repository;
        }
        _editor.Text = _original.EditorExecutable ?? "";
        _timeout.Value = Math.Clamp(_original.OperationTimeoutMinutes, 1, 120);
        _snapshotBytes.Value = Math.Clamp(_original.SnapshotLimits.MaxBytes, 1, 2147483648L);
        _snapshotEntries.Value = Math.Clamp(_original.SnapshotLimits.MaxEntries, 1, 50000);
        _timings.Checked = _original.EnableLocalTimings;
    }

    private static IEnumerable<DataGridViewRow> FilledRows(DataGridView grid)
    {
        grid.EndEdit();
        return grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow
            && row.Cells.Cast<DataGridViewCell>().Any(cell => !string.IsNullOrWhiteSpace(cell.Value?.ToString())));
    }

    private static string Cell(DataGridViewRow row, int index) => row.Cells[index].Value?.ToString()?.Trim() ?? "";

    private void Save()
    {
        try
        {
            AppSettings edited = new()
            {
                Repositories = FilledRows(_github).Select(row =>
                    (row.Tag as RepositoryRegistration ?? new("", "", "")) with
                    { RepositoryKey = Cell(row, 0), LocalPath = Cell(row, 1), WorkspaceRoot = Cell(row, 2) }).ToList(),
                GitRepositories = FilledRows(_git).Select(row =>
                    (row.Tag as GitRepositoryRegistration ?? new("", "", "")) with
                    { CloneUrl = Cell(row, 0), LocalPath = Cell(row, 1), WorkspaceRoot = Cell(row, 2) }).ToList(),
                EditorExecutable = string.IsNullOrWhiteSpace(_editor.Text) ? null : _editor.Text.Trim(),
                OperationTimeoutMinutes = (int)_timeout.Value,
                EnableLocalTimings = _timings.Checked,
                SnapshotLimits = new SnapshotExportLimits
                {
                    MaxBytes = (long)_snapshotBytes.Value,
                    MaxEntries = (int)_snapshotEntries.Value,
                },
                OtherSettings = _original.OtherSettings,
            };
            _fingerprint = SettingsStore.Save(edited, _fingerprint);
            _original = edited;
            MessageBox.Show(this, "Saved. Existing files were preserved; the previous settings are in settings.json.previous. "
                + "Restart the tray to apply snapshot-limit changes.", "Settings saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Settings were not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Reload()
    {
        if (MessageBox.Show(this, "Discard unsaved edits and reload settings?", "Reload settings",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            (_original, _fingerprint) = SettingsStore.ReadForEdit();
            LoadValues();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Could not reload settings"); }
    }

    private void BrowseFolder(DataGridView grid)
    {
        if (grid.CurrentCell is null || grid.CurrentCell.ColumnIndex == 0)
        {
            MessageBox.Show(this, "Select a local clone or workspace-root cell first.");
            return;
        }
        using FolderBrowserDialog picker = new() { Description = "Select the folder for this cell" };
        if (picker.ShowDialog(this) == DialogResult.OK) grid.CurrentCell.Value = picker.SelectedPath;
    }

    private async Task TestOriginAsync(DataGridView grid, bool generic, Button button)
    {
        if (grid.CurrentRow is null || grid.CurrentRow.IsNewRow) return;
        string identity = Cell(grid.CurrentRow, 0);
        string source = Cell(grid.CurrentRow, 1);
        button.Enabled = false;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string origin = await Task.Run(() => new GitCliWorkspaceGit().GetOriginUrlAsync(source, timeout.Token));
            bool matches = generic
                ? GitBranchRequest.OriginMatches(origin, identity)
                : PullRequestReference.TryParse("https://github.com/" + identity + "/pull/1", out PullRequestReference? request)
                    && GitHubRemoteIdentity.Matches(origin, request);
            MessageBox.Show(this, matches ? "Origin matches. No fetch or checkout was performed."
                : "Origin does not match this registration. No files were changed.", "Repository origin",
                MessageBoxButtons.OK, matches ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            if (!IsDisposed) MessageBox.Show(this, exception.Message, "Origin check failed");
        }
        finally { if (!button.IsDisposed) button.Enabled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed)
        {
            _lifetimeDisposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
