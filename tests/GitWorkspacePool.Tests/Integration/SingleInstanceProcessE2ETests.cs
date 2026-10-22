using System.Diagnostics;
using GitWorkspacePool.Core.Protocol;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Real Windows tray executables, real mutex and user-scoped named pipe.
/// An isolated CLI mode avoids user settings, registry changes and UI.
/// </summary>
public sealed class SingleInstanceProcessE2ETests
{
    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task SecondTrayProcess_ForwardsVersionedProtocolToExistingInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateFixtureRoot();
        string nonce = Guid.NewGuid().ToString("N");
        string ready = Path.Combine(root, "ready");
        string received = Path.Combine(root, "received");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using Process primary = StartTray("--ipc-e2e-host", nonce, ready, received);

        try
        {
            await WaitForReadyAsync(ready, primary, timeout.Token);
            string url = "https://github.com/owner/repo/pull/17";
            string message = "prworkspace://open?url=" + Uri.EscapeDataString(url) + "&v=1";
            using Process secondary = StartTray("--ipc-e2e-forward", nonce, message);

            await secondary.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, secondary.ExitCode);
            await primary.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, primary.ExitCode);

            string forwarded = File.ReadAllText(received);
            Assert.Equal(message, forwarded);
            Assert.True(OpenRequestProtocol.TryExtract(forwarded, out string parsed));
            Assert.Equal(url, parsed);
        }
        finally
        {
            await StopAsync(primary);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task SecondTrayProcess_FailsWhenPrimaryOwnsMutexButPipeIsAbsent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateFixtureRoot();
        string nonce = Guid.NewGuid().ToString("N");
        string ready = Path.Combine(root, "ready");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using Process primary = StartTray("--ipc-e2e-hold", nonce, ready);

        try
        {
            await WaitForReadyAsync(ready, primary, timeout.Token);
            string message = "prworkspace://open?url="
                + Uri.EscapeDataString("https://github.com/owner/repo/pull/17")
                + "&v=1";
            using Process secondary = StartTray("--ipc-e2e-forward", nonce, message);
            await secondary.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, secondary.ExitCode);
            Assert.False(primary.HasExited);
        }
        finally
        {
            await StopAsync(primary);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFixtureRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "GitWorkspacePool.SingleInstance." + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        return root;
    }

    private static Process StartTray(params string[] args)
    {
        string configuration = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
        ).Parent!.Name;
        string executable = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "GitWorkspacePool.Tray", "bin", configuration,
                "net10.0-windows", "win-x64", "GitWorkspacePool.Tray.exe"
            )
        );
        Assert.True(
            File.Exists(executable),
            "Build GitWorkspacePool.slnx before running the process E2E: " + executable
        );

        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)
            ?? throw new InvalidOperationException("Could not launch isolated tray process.");
    }

    private static async Task WaitForReadyAsync(
        string path, Process primary, CancellationToken cancellationToken
    )
    {
        while (!File.Exists(path))
        {
            Assert.False(primary.HasExited, "Primary tray probe exited before signaling readiness.");
            await Task.Delay(30, cancellationToken);
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }
}
