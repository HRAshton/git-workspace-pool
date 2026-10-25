using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure;

if (args.Length == 4 && args[0] == "--hold-lock-fixture")
{
    await using IAsyncDisposable heldLock = await new FileWorkspaceOperationLock(args[1]).AcquireAsync(args[2], default);
    await File.WriteAllTextAsync(args[3], "locked");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

// Disposable fixture process only. Never reads a real profile or launches the tray.
if (args.Length != 8 || args[0] != "--warm-crash-fixture") return 2;
string source = Path.GetFullPath(args[1]);
string poolRoot = Path.GetFullPath(args[2]);
string database = Path.GetFullPath(args[3]);
string id = args[4];
string target = args[5];
string checkpoint = args[6];
string ready = Path.GetFullPath(args[7]);
GitRepositoryRegistration repository = new("https://example.invalid/team/repo.git", source, poolRoot);
SqliteWarmWorkspaceRegistry registry = new(database);
await using IAsyncDisposable held = await new FileWorkspaceOperationLock(Path.Combine(Path.GetDirectoryName(database)!, "crash-locks"))
    .AcquireAsync(source, default);
WarmWorkspace workspace = registry.List(repository.RepositoryIdentity).Single(row => row.Id == id);
WarmWorktreeGit git = new();
await git.VerifySourceAsync(repository, default);
await git.VerifyAvailableAsync(repository, workspace, target, default);
registry.BeginTransition(id, "feature", target);
if (checkpoint == "after-checkout") await git.TransitionAsync(workspace.Path, target, default);
else if (checkpoint == "during-checkout")
{
    string admin = (await File.ReadAllTextAsync(Path.Combine(workspace.Path, ".git"))).Trim()[8..];
    string indexLock = Path.Combine(admin, "index.lock");
    Task transition = git.TransitionAsync(workspace.Path, target, default);
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
    while (!File.Exists(indexLock))
    {
        if (transition.IsCompleted) { await transition; return 3; }
        await Task.Delay(1, deadline.Token);
    }
}
else if (checkpoint != "after-intent") return 2;
await File.WriteAllTextAsync(ready, "durable");
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
