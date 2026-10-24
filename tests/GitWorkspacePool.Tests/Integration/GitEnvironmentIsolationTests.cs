using System.Diagnostics;
using GitWorkspacePool.Infrastructure;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

[CollectionDefinition("Git environment isolation", DisableParallelization = true)]
public sealed class GitEnvironmentIsolationCollection { }

[Collection("Git environment isolation")]
public sealed class GitEnvironmentIsolationTests
{
    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ParentRepositoryConfigurationAndTraceEnvironmentCannotRedirectAppGit()
    {
        string root = Path.Combine(Path.GetTempPath(), "gwp-git-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string actual = Path.Combine(root, "actual");
        string foreign = Path.Combine(root, "foreign");
        Dictionary<string, string?> original = [];
        try
        {
            await GitAsync(root, "init", actual);
            await GitAsync(root, "init", foreign);
            await GitAsync(actual, "remote", "add", "origin", "https://bitbucket.example.com/scm/team/actual.git");
            await GitAsync(foreign, "remote", "add", "origin", "https://foreign.invalid/foreign.git");
            Dictionary<string, string> injected = new()
            {
                ["GIT_DIR"] = Path.Combine(foreign, ".git"),
                ["GIT_WORK_TREE"] = foreign,
                ["GIT_INDEX_FILE"] = Path.Combine(foreign, ".git", "foreign-index"),
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "remote.origin.url",
                ["GIT_CONFIG_VALUE_0"] = "https://injected.invalid/repo.git",
                ["GIT_TRACE"] = Path.Combine(root, "unexpected.trace"),
                ["GIT_SSL_NO_VERIFY"] = "1",
            };
            foreach (var value in injected)
            {
                original[value.Key] = Environment.GetEnvironmentVariable(value.Key);
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }
            string origin = await new GitCliWorkspaceGit().GetOriginUrlAsync(actual, CancellationToken.None);
            Assert.Equal("https://bitbucket.example.com/scm/team/actual.git", origin);
            Assert.False(File.Exists(Path.Combine(root, "unexpected.trace")));
            Assert.False(File.Exists(Path.Combine(foreign, ".git", "foreign-index")));
        }
        finally
        {
            foreach (var value in original) Environment.SetEnvironmentVariable(value.Key, value.Value);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static async Task GitAsync(string directory, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        string failure = await error;
        Assert.True(process.ExitCode == 0, failure);
    }
}
