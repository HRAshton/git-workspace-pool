using Microsoft.Win32;

namespace GitWorkspacePool.Tray;

internal static class ProtocolRegistration
{
    public static void RegisterCurrentUser()
    {
        string applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");

        if (!applicationPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Start the compiled Windows application executable.");
        }

        using RegistryKey protocol = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\prworkspace",
            true
        );
        protocol.SetValue(string.Empty, "URL:Git Workspace Pool Protocol");
        protocol.SetValue("URL Protocol", string.Empty);

        using RegistryKey command = protocol.CreateSubKey(@"shell\open\command", true);
        command.SetValue(string.Empty, $"\"{applicationPath}\" \"%1\"");
    }
}
