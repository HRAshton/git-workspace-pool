using System.Text.RegularExpressions;

namespace GitWorkspacePool.Tray.Settings;

/// <summary>Shows Git's live transfer stages without interpreting warnings as failures.</summary>
internal sealed class GitCloneProgressForm : Form
{
    private readonly Label _stage = new() { Dock = DockStyle.Top, AutoSize = false, Height = 42, Text = "Starting Git clone..." };
    private readonly Label _warning = new() { Dock = DockStyle.Top, AutoSize = false, Height = 42, ForeColor = System.Drawing.Color.DarkGoldenrod };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Style = ProgressBarStyle.Marquee };
    private readonly TextBox _messages = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _cancel = new() { Text = "Cancel clone", Dock = DockStyle.Bottom, Height = 38 };
    private bool _working = true;

    public event EventHandler? CancelRequested;

    public GitCloneProgressForm()
    {
        Text = "Cloning Git repository";
        Width = 750;
        Height = 350;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_messages);
        Controls.Add(_warning);
        Controls.Add(_bar);
        Controls.Add(_stage);
        Controls.Add(_cancel);
        _cancel.Click += (_, _) => RequestCancellation();
        FormClosing += (_, args) =>
        {
            if (!_working) return;
            args.Cancel = true;
            RequestCancellation();
        };
    }

    public void Stage(string message)
    {
        if (IsDisposed) return;
        _stage.Text = message;
        _bar.Style = ProgressBarStyle.Marquee;
    }

    public void Report(string raw)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(raw)) return;
        // Never render control characters from a remote Git server as UI instructions.
        string message = new(raw.Where(character => !char.IsControl(character)).Take(1024).ToArray());
        if (message.Length == 0) return;
        _messages.AppendText(message + Environment.NewLine);
        if (_messages.TextLength > 18000)
        {
            _messages.Text = _messages.Text[^10000..];
            _messages.SelectionStart = _messages.TextLength;
            _messages.ScrollToCaret();
        }

        if (message.Contains("warning", StringComparison.OrdinalIgnoreCase))
        {
            _warning.Text = message.Length > 180 ? message[..180] + "..." : message;
            return;
        }

        _stage.Text = message.Length > 180 ? message[..180] + "..." : message;
        Match match = Regex.Match(message, @"(?<!\d)(\d{1,3})%");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent <= 100)
        {
            _bar.Style = ProgressBarStyle.Blocks;
            _bar.Value = percent;
        }
        else
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }
    }

    public void Finish()
    {
        _working = false;
        _cancel.Enabled = false;
    }

    private void RequestCancellation()
    {
        if (!_cancel.Enabled) return;
        _cancel.Enabled = false;
        _stage.Text = "Stopping Git. Any incomplete clone directory will be retained.";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
