using System.Drawing;
namespace GitWorkspacePool.Tray.Settings;

internal sealed class WorkspaceOperationForm : Form
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Button _cancel = new() { Text = "Cancel", Width = 100 };
    private bool _completed;

    public WorkspaceOperationForm(string title, CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        Text = title;
        Width = 520;
        Height = 175;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Label status = new()
        {
            Text = title + "...", Left = 16, Top = 14, Width = 470, Height = 24,
            AccessibleName = "Operation status",
        };
        ProgressBar progress = new()
        {
            Style = ProgressBarStyle.Marquee, Left = 16, Top = 46, Width = 470, Height = 16,
            AccessibleName = "Operation in progress",
        };
        _cancel.Left = 386;
        _cancel.Top = 82;
        void Cancel()
        {
            _cancel.Enabled = false;
            status.Text = "Cancelling; unfinished work will be preserved...";
            _cancellation.Cancel();
        }
        _cancel.Click += (_, _) => Cancel();
        FormClosing += (_, args) =>
        {
            if (!_completed)
            {
                args.Cancel = true;
                Cancel();
            }
        };
        Controls.AddRange([status, progress, _cancel]);
    }

    public void Complete()
    {
        _completed = true;
        Close();
    }
}
