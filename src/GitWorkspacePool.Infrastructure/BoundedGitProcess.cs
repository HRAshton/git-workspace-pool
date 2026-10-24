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
        CancellationToken cancellationToken
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
        Task<string> error = ReadBoundedAsync(process.StandardError, 1024 * 1024, process, cancellationToken);
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
                if (count > limit - text.Length) throw new InvalidOperationException("Git preview output exceeded its safety limit.");
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
