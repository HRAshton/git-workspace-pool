using System.Text.Json;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Tray.Infrastructure;

namespace GitWorkspacePool.Tray.Settings;

internal sealed class WorkspaceSettingsForm : Form
{
    private readonly TextBox _json = new() { Multiline = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private string _fingerprint;

    public WorkspaceSettingsForm()
    {
        Text = "Git Workspace Pool settings"; Width = 900; Height = 600;
        var snapshot = SettingsStore.ReadForEdit();
        AppSettings settings = snapshot.Settings;
        _fingerprint = snapshot.Fingerprint;
        _json.Text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 45 };
        Button save = new() { Text = "Save", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                AppSettings edited = JsonSerializer.Deserialize<AppSettings>(_json.Text) ?? throw new InvalidOperationException("Expected settings object.");
                _fingerprint = SettingsStore.Save(edited, _fingerprint);
                MessageBox.Show(this, "Settings saved.");
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Settings not saved"); }
        };
        Button register = new() { Text = "Register repository...", AutoSize = true };
        register.Click += async (_, _) =>
        {
            using RepositoryRegistrationForm form = new(null);
            if (form.ShowDialog(this) != DialogResult.OK || form.Registration is not GitRepositoryRegistration repository) return;
            register.Enabled = false;
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(Math.Max(60, SettingsStore.Read().OperationTimeoutMinutes)));
                await RepositoryOnboarding.RegisterAsync(repository, form.CreateBareClone, timeout.Token);
                var updated = SettingsStore.ReadForEdit();
                _fingerprint = updated.Fingerprint;
                _json.Text = JsonSerializer.Serialize(updated.Settings, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Repository not registered"); }
            finally { register.Enabled = true; }
        };
        buttons.Controls.AddRange([save, register]); Controls.Add(_json); Controls.Add(buttons);
    }
}
