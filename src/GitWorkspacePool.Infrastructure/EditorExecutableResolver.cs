namespace GitWorkspacePool.Infrastructure;

/// <summary>Resolves code.exe explicitly on PATH, without a shell, commands or executable lookup in the workspace.</summary>
public static class EditorExecutableResolver
{
    public static string Resolve(string? configured)
    {
        string value = string.IsNullOrWhiteSpace(configured) ? "code.exe" : configured.Trim();
        if (Path.IsPathFullyQualified(value))
            return File.Exists(value) ? value : throw new InvalidOperationException("Configured editor executable does not exist.");
        if (!value.Equals("code.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use code.exe from PATH or an absolute executable path.");
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            string path = Path.Combine(directory, "code.exe");
            if (File.Exists(path)) return Path.GetFullPath(path);
            // VS Code commonly adds its bin directory containing code.cmd to PATH.
            // Resolve its sibling Code.exe directly; never execute a .cmd file or shell.
            if (Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                string app = Path.GetFullPath(Path.Combine(directory, "..", "Code.exe"));
                if (File.Exists(app)) return app;
            }
        }
        throw new InvalidOperationException("code.exe was not found on PATH. Add the VS Code application folder to PATH or select Code.exe in Settings.");
    }
}
