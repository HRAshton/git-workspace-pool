using System.Drawing;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Diagnostics;

namespace GitWorkspacePool.Tray.Settings;

internal sealed class WorkspaceTimingsForm : Form
{
    public WorkspaceTimingsForm(LocalOperationTimings timings)
    {
        Text = "Local operation timings";
        Width = 820;
        Height = 430;
        StartPosition = FormStartPosition.CenterScreen;
        DataGridView grid = new()
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        foreach (string column in new[] { "Operation", "Completed", "Cancelled", "Failed", "Median ms", "p95 ms" })
            grid.Columns.Add(column, column);
        var report = timings.Read();
        foreach (var group in report.Samples.GroupBy(sample => sample.Operation))
        {
            double[] completed = group.Where(sample => sample.Outcome == WorkspaceOperationOutcome.Completed)
                .Select(sample => sample.ElapsedMilliseconds).ToArray();
            grid.Rows.Add(group.Key.ToString(), completed.Length,
                group.Count(sample => sample.Outcome == WorkspaceOperationOutcome.Cancelled),
                group.Count(sample => sample.Outcome == WorkspaceOperationOutcome.Failed),
                completed.Length == 0 ? "n/a" : LocalOperationTimings.Percentile(completed, 0.5).ToString("F1"),
                completed.Length == 0 ? "n/a" : LocalOperationTimings.Percentile(completed, 0.95).ToString("F1"));
        }
        Label notes = new()
        {
            Dock = DockStyle.Bottom, Height = 82, Padding = new Padding(12),
            Text = report.Samples.Count + " local samples; " + report.UnreadableRecords + " unreadable records. "
                + "Enable recording in Settings. These times cover application work until the operation returns, "
                + "not browser prompts or visible VS Code startup. No URLs, repository paths, branches or error messages are recorded. "
                + "No measurements are sent over the network.",
        };
        Controls.Add(grid);
        Controls.Add(notes);
    }
}
