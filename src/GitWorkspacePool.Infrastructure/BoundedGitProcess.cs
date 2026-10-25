using System.Diagnostics;
using System.Text;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Bounded Git CLI execution with the application safety environment and no shell expansion.</summary>
internal static class BoundedGitProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        int maxCharacters,
        CancellationToken cancellationToken,
        IProgress<string>? stderrProgress = null
    )
    {
        ProcessStartInfo start = new("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
        };
        GitProcessSafety.Configure(start);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        Task<string> output = ReadBoundedAsync(process.StandardOutput, maxCharacters, process, cancellationToken);
        // Git emits both progress and harmless SSH/server warnings on stderr, even when successful.
        // Do not buffer an entire years-long clone's progress stream or mistake its size for failure.
        Task<string> error = ReadStderrAsync(process.StandardError, stderrProgress, process, cancellationToken);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken));
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> ReadStderrAsync(
        StreamReader reader, IProgress<string>? progress, Process process, CancellationToken cancellationToken)
    {
        const int retainedLimit = 64 * 1024;
        const int lineLimit = 4096;
        try
        {
            StringBuilder retained = new();
            StringBuilder line = new();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                if (count > retainedLimit - retained.Length)
                    retained.Remove(0, count - (retainedLimit - retained.Length));
                retained.Append(buffer, 0, count);
                if (progress is null) continue;
                for (int i = 0; i < count; i++)
                {
                    char character = buffer[i];
                    if (character is '\r' or '\n')
                    {
                        if (line.Length > 0)
                        {
                            progress.Report(line.ToString().Trim());
                            line.Clear();
                        }
                    }
                    else if (!char.IsControl(character) && line.Length < lineLimit)
                    {
                        line.Append(character);
                    }
                }
            }
            if (progress is not null && line.Length > 0) progress.Report(line.ToString().Trim());
            return retained.ToString();
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, int limit, Process process, CancellationToken cancellationToken
    )
    {
        try
        {
            StringBuilder text = new();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                if (count > limit - text.Length) throw new InvalidOperationException("Git output exceeded its safety limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
