using System.Drawing;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Tray.Settings;

internal sealed class WorkspaceReuseChoiceForm : Form
{
    private readonly DataGridView _candidates = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
    };
    private readonly CheckBox _copyNpm = new()
    {
        Text = "Copy compatible npm cache after checkout (optional; final checks may refuse)",
        AutoSize = true,
    };

    public WorkspaceReuseChoiceForm(GitBranchReusePlan plan)
    {
        Text = "Verified released-slot choices";
        Width = 1000;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        Label explanation = new()
        {
            Dock = DockStyle.Top, Height = 78, Padding = new Padding(12),
            Text = "Branch: " + plan.Request.Branch + " | Captured commit: " + plan.TargetCommit + "\n"
                + "Each choice is an empty released slot. Original files stay parked. A fresh checkout is still required. "
                + "This preview does not claim a measured speedup; cache copying is optional.",
        };
        foreach (string header in new[] { "Retained donor", "Changed paths", "npm cache", "Cache MiB", "Slot" })
            _candidates.Columns.Add(header, header);
        foreach (VerifiedReuseCandidate candidate in plan.Candidates)
        {
            int row = _candidates.Rows.Add(candidate.DonorLabel, candidate.ChangedPaths, candidate.ArtifactStatus,
                candidate.NpmBytes is long bytes ? (bytes / (1024.0 * 1024)).ToString("F1") : "unknown",
                candidate.WorkspacePath);
            _candidates.Rows[row].Tag = candidate;
        }
        _candidates.SelectionChanged += (_, _) =>
        {
            _copyNpm.Checked = false;
            _copyNpm.Enabled = SelectedCandidate?.CanCopyNpm == true;
        };
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 82, Padding = new Padding(8) };
        actions.Controls.Add(_copyNpm);
        Button use = new() { Text = "Use selected slot", AutoSize = true, DialogResult = DialogResult.Yes,
            Enabled = plan.Candidates.Count != 0 };
        Button fresh = new() { Text = "Open fresh normally", AutoSize = true, DialogResult = DialogResult.No };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([use, fresh, cancel]);
        Controls.Add(_candidates);
        Controls.Add(explanation);
        Controls.Add(actions);
        AcceptButton = use;
        CancelButton = cancel;
        if (_candidates.Rows.Count > 0) _candidates.CurrentCell = _candidates.Rows[0].Cells[0];
        _copyNpm.Enabled = SelectedCandidate?.CanCopyNpm == true;
    }

    public VerifiedReuseCandidate? SelectedCandidate => _candidates.CurrentRow?.Tag as VerifiedReuseCandidate;
    public bool CopyNpm => _copyNpm.Checked && _copyNpm.Enabled;
}
