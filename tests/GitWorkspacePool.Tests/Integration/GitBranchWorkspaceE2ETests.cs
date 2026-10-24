using System.Diagnostics;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

public sealed class GitBranchWorkspaceE2ETests
{
    private const string CloneUrl = "https://bitbucket.example.com/stash/scm/team/repo.git";
    private static readonly GitBranchRequest Request = new(CloneUrl, "feature/work");

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ProtocolToRealGitPinsCommitAndReopenPreservesEveryKindOfLocalEdit()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Assert.True(GitBranchProtocol.TryParse(GitBranchProtocolTests.Link(CloneUrl, Request.Branch), out GitBranchRequest? request));
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(request, CancellationToken.None);
        Assert.False(first.Reused);
        Assert.Equal(fixture.Commit, await Fixture.GitAsync(first.WorkspacePath, "rev-parse", "HEAD"));
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "staged");
        await Fixture.GitAsync(first.WorkspacePath, "add", "tracked.txt");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "unstaged");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "untracked.txt"), "keep untracked");
        File.WriteAllBytes(Path.Combine(first.WorkspacePath, "ignored.bin"), [0, 1, 255]);
        string before = await Fixture.GitAsync(first.WorkspacePath, "status", "--porcelain", "--ignored");
        // A new handler and store simulate tray restart. A nonexistent remote must not be fetched on reopen.
        await Fixture.GitAsync(fixture.Source, "remote", "set-url", "origin", Path.Combine(fixture.Root, "missing.git"));
        OpenPullRequestResult reopened = await fixture.Handler().HandleAsync(request, CancellationToken.None);
        Assert.True(reopened.Reused);
        Assert.Equal(first.WorkspacePath, reopened.WorkspacePath);
        Assert.Equal(before, await Fixture.GitAsync(first.WorkspacePath, "status", "--porcelain", "--ignored"));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(first.WorkspacePath, "tracked.txt")));
        Assert.Equal("staged", await Fixture.GitAsync(first.WorkspacePath, "show", ":tracked.txt"));
        Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(Path.Combine(first.WorkspacePath, "ignored.bin")));
        Assert.Equal(2, fixture.Editor.Opened.Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task MissingBranchRetainsPreparingAndCanRetryWithoutDeletingAnything()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchRequest missing = new(CloneUrl, "missing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(missing, CancellationToken.None));
        GitBranchSession pending = (await fixture.Store().FindAsync(missing, CancellationToken.None))!;
        Assert.Equal(WorkspaceSessionState.Preparing, pending.State);
        Assert.Null(pending.PinnedCommit);
        Assert.False(Directory.Exists(pending.WorkspacePath));
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/missing");
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(missing, CancellationToken.None);
        Assert.False(opened.Reused);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(missing, CancellationToken.None))!.State);
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCheckoutOnlyReconcilesCleanPinnedOwnedWorktree(bool dirty)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await fixture.Store().PinAsync(session.OperationId, fixture.Commit, CancellationToken.None);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, session.WorkspacePath, fixture.Commit, CancellationToken.None);
        if (dirty) File.WriteAllText(Path.Combine(session.WorkspacePath, "tracked.txt"), "user work after interruption");
        if (dirty)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
            Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
            Assert.Equal("user work after interruption", File.ReadAllText(Path.Combine(session.WorkspacePath, "tracked.txt")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
            Assert.Empty(fixture.Editor.Opened);
        }
        else
        {
            Assert.True((await fixture.Handler().HandleAsync(Request, CancellationToken.None)).Reused);
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task PinnedBeforeCrashCreatesOriginalCommitEvenWhenRemoteBranchMoves()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await fixture.Store().PinAsync(session.OperationId, fixture.Commit, CancellationToken.None);
        File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "new remote head");
        await Fixture.GitAsync(fixture.Source, "add", "tracked.txt");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "advance branch");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work");
        OpenPullRequestResult result = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        Assert.Equal(fixture.Commit, await Fixture.GitAsync(result.WorkspacePath, "rev-parse", "HEAD"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(result.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task UnknownOccupiedPathIsNeverAdoptedOrDeleted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string path = Path.Combine(fixture.Repository.WorkspaceRoot, Request.WorkspaceName);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "foreign.txt"), "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.Null(await fixture.Store().FindAsync(Request, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "foreign.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task WrongRealOriginRejectedBeforeReservationOrWorkspaceCreation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler(new GitCliWorkspaceGit()).HandleAsync(Request, CancellationToken.None));
        Assert.Null(await fixture.Store().FindAsync(Request, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Repository.WorkspaceRoot));
        Assert.Empty(fixture.Editor.Opened);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ConcurrentClicksShareOneDurableOwnerAndOneCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => fixture.Handler().HandleAsync(Request, CancellationToken.None))));
        Assert.Single(results.Where(r => !r.Reused));
        Assert.Single(results.Select(r => r.WorkspacePath).Distinct());
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData("credential.helper", "!echo unsafe")]
    [InlineData("http.sslVerify", "false")]
    [InlineData("http.https://bitbucket.example.com/.sslVerify", "false")]
    [InlineData("core.sshCommand", "unsafe")]
    [InlineData("url.https://elsewhere.invalid/.insteadOf", "https://bitbucket.example.com/")]
    public async Task BranchFetchRejectsUnsafeGitConfiguration(string key, string value)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "config", "--local", key, value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitCliWorkspaceGit().FetchBranchCommitAsync(
            fixture.Source, Request.Branch, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task StoreUsesCaseSensitiveBranchesAndRejectsOwnershipAndPinReplacement()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession first = await fixture.ReserveAsync(Request);
        GitBranchSession other = await fixture.ReserveAsync(new(CloneUrl, "feature/Work"));
        Assert.NotEqual(first.OperationId, other.OperationId);
        Assert.NotEqual(first.WorkspacePath, other.WorkspacePath);
        await fixture.Store().PinAsync(first.OperationId, fixture.Commit, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().PinAsync(first.OperationId, new string('a', 40), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().ReserveAsync(Request,
            fixture.Repository with { LocalPath = Path.Combine(fixture.Root, "different") }, first.WorkspacePath, CancellationToken.None));
        Assert.Equal(2, (await fixture.Store().ListUnfinishedAsync(CancellationToken.None)).Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task MissingActiveWorktreeIsQuarantinedInsteadOfRecreated()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        string retained = first.WorkspacePath + "-retained";
        Directory.Move(first.WorkspacePath, retained);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.False(Directory.Exists(first.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(retained, "tracked.txt")));
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task UnpinnedExistingCheckoutIsQuarantinedInsteadOfAdopted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitBranchSession session = await fixture.ReserveAsync(Request);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, session.WorkspacePath, fixture.Commit, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        Assert.Equal(WorkspaceSessionState.RecoveryRequired, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        Assert.True(File.Exists(Path.Combine(session.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task DifferentBranchesOpenIndependentlyWithNamedCredentialHelper()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "config", "credential.helper", "manager");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        OpenPullRequestResult[] opened = await Task.WhenAll(
            Task.Run(() => fixture.Handler().HandleAsync(Request, CancellationToken.None)),
            Task.Run(() => fixture.Handler().HandleAsync(new(CloneUrl, "other"), CancellationToken.None)));
        Assert.NotEqual(opened[0].WorkspacePath, opened[1].WorkspacePath);
        Assert.All(opened, result => Assert.False(result.Reused));
        foreach (OpenPullRequestResult result in opened)
            Assert.Equal(fixture.Commit, await Fixture.GitAsync(result.WorkspacePath, "rev-parse", "HEAD"));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task SharedLifecycleSuspendsSnapshotsRestoresAndParksBranchWithoutChangingEdits()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession branch = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "tracked.txt"), "staged branch data");
        await Fixture.GitAsync(opened.WorkspacePath, "add", "tracked.txt");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "tracked.txt"), "unstaged branch data");
        File.WriteAllBytes(Path.Combine(opened.WorkspacePath, "ignored.bin"), [5, 0, 255]);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "untracked.txt"), "untracked branch data");
        string status = await Fixture.GitAsync(opened.WorkspacePath, "status", "--porcelain", "--ignored");
        SqliteWorkspaceSessionJournal journal = fixture.Journal();
        WorkspaceSession shared = (await journal.FindAsync(branch.OperationId, CancellationToken.None))!;
        Assert.Equal(Request.Branch, shared.BranchName);
        Assert.Equal(CloneUrl, shared.CloneUrl);
        Assert.Contains(Request.Branch, shared.DisplayName);
        Assert.DoesNotContain("PR #", shared.DisplayName);
        await new SuspendWorkspaceSessionService(journal, journal, fixture.Locks(),
            new GitCliWorkspaceGit(), new WorkspaceDirectories()).SuspendAsync(branch.OperationId, CancellationToken.None);
        Assert.Equal(WorkspaceSessionState.Suspended, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        Assert.True((await fixture.Handler().HandleAsync(Request, CancellationToken.None)).Reused);
        CaptureWorkspaceSnapshotService snapshots = new(journal, fixture.Locks(), new GitCliWorkspaceGit(),
            new VerifiedWorkspaceSnapshotExporter(Path.Combine(fixture.Root, "snapshots"), new GitWorktreeSnapshotScanner()));
        WorkspaceSnapshotReceipt receipt = await snapshots.CaptureAsync(branch.OperationId, CancellationToken.None);
        RestoreWorkspaceSnapshotService restore = new(journal, new VerifiedWorkspaceSnapshotRestorer(
            Path.Combine(fixture.Root, "recovered"), new GitCliWorkspaceGit(), new GitWorktreeSnapshotScanner(), journal));
        string recovered = await restore.RestoreAsync(receipt.SnapshotId, CancellationToken.None);
        Assert.Equal(status, await Fixture.GitAsync(recovered, "status", "--porcelain", "--ignored"));
        Assert.Equal("staged branch data", await Fixture.GitAsync(recovered, "show", ":tracked.txt"));
        Assert.Equal("unstaged branch data", File.ReadAllText(Path.Combine(recovered, "tracked.txt")));
        Assert.Equal(new byte[] { 5, 0, 255 }, File.ReadAllBytes(Path.Combine(recovered, "ignored.bin")));
        string parked = await fixture.Parking().ParkAsync(branch.OperationId, CancellationToken.None);
        Assert.Equal(status, await Fixture.GitAsync(parked, "status", "--porcelain", "--ignored"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(Request, CancellationToken.None));
        await fixture.Parking().UnparkAsync(branch.OperationId, CancellationToken.None);
        Assert.Equal(status, await Fixture.GitAsync(opened.WorkspacePath, "status", "--porcelain", "--ignored"));
        Assert.True((await fixture.Handler().HandleAsync(Request, CancellationToken.None)).Reused);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task BranchReleaseReuseAndNpmTransferPreserveDonorAndReopenBothOwners()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Source, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(fixture.Source, "package-lock.json"), "{\"lockfileVersion\":3}");
        File.AppendAllText(Path.Combine(fixture.Source, ".gitignore"), "node_modules/\n");
        await Fixture.GitAsync(fixture.Source, "add", ".");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "npm manifests");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work", "HEAD:refs/heads/other");
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession donor = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        Directory.CreateDirectory(Path.Combine(first.WorkspacePath, "node_modules", "fixture"));
        File.WriteAllText(Path.Combine(first.WorkspacePath, "node_modules", "fixture", "index.js"), "preserve cached dependency");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "donor edits");
        string parked = await fixture.Parking().ParkAsync(donor.OperationId, CancellationToken.None);
        WorkspaceSlot slot = await fixture.ReleaseAsync(donor.OperationId);
        Assert.Equal(WorkspaceSlotState.Ready, slot.State);
        GitBranchRequest other = new(CloneUrl, "other");
        OpenPullRequestResult second = await fixture.Handler().HandleInReadySlotAsync(
            other, slot.SlotId, slot.Generation, CancellationToken.None);
        Assert.Equal(first.WorkspacePath, second.WorkspacePath);
        Assert.Equal("donor edits", File.ReadAllText(Path.Combine(parked, "tracked.txt")));
        GitBranchSession recipient = (await fixture.Store().FindAsync(other, CancellationToken.None))!;
        WarmArtifactTransferReceipt transfer = await new TransferVerifiedWarmArtifactsService(
            fixture.Journal(), fixture.Locks(), new GitCliWorkspaceGit(), new VerifiedNpmArtifactCopier()
        ).TransferAsync(donor.OperationId, recipient.OperationId, CancellationToken.None);
        Assert.Equal(1, transfer.FileCount);
        Assert.Equal("preserve cached dependency", File.ReadAllText(Path.Combine(second.WorkspacePath, "node_modules", "fixture", "index.js")));
        Assert.Equal("preserve cached dependency", File.ReadAllText(Path.Combine(parked, "node_modules", "fixture", "index.js")));
        Assert.Equal(parked, (await fixture.Handler().HandleAsync(Request, CancellationToken.None)).WorkspacePath);
        Assert.Equal(second.WorkspacePath, (await fixture.Handler().HandleAsync(other, CancellationToken.None)).WorkspacePath);
        Assert.Equal(WorkspaceSessionState.Released, (await fixture.Store().FindAsync(Request, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Store().FindAsync(other, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task InterruptedBranchSlotRequiresExplicitGenerationCheckedRecovery()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession old = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        await fixture.Parking().ParkAsync(old.OperationId, CancellationToken.None);
        WorkspaceSlot slot = await fixture.ReleaseAsync(old.OperationId);
        GitBranchRequest other = new(CloneUrl, "other");
        int reference = await fixture.Store().GetReferenceNumberAsync(other, CancellationToken.None);
        WorkspaceReadySlotReservation reservation = (await fixture.Slots().TryReserveAsync(
            slot.SlotId, slot.Generation, other.RepositoryIdentity, reference, fixture.Source, CancellationToken.None))!;
        await fixture.Journal().PinCommitAsync(reservation.Session.OperationId, fixture.Commit, CancellationToken.None);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, slot.WorkspacePath, fixture.Commit, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler().HandleAsync(other, CancellationToken.None));
        WorkspaceReadySlotReservation selected = (await fixture.Slots().FindReservedAsync(slot.SlotId, CancellationToken.None))!;
        Assert.Equal(other.Branch, selected.Session.BranchName);
        ReconcileExplicitReadySlotService recover = new(fixture.Slots(), fixture.Slots(),
            new WorkspaceReadySlotSafety(), new VerifiedReadySlotCheckout(), new GitCliWorkspaceGit(), fixture.Locks());
        await recover.ReconcileSelectedAsync(selected, fixture.Repository, CancellationToken.None);
        Assert.True((await fixture.Handler().HandleAsync(other, CancellationToken.None)).Reused);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task LegacyBranchUpgradeKeepsOperationPathStateAndExistingPrSnapshotReferences()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid legacyId = Guid.NewGuid();
        string path = Path.Combine(fixture.Repository.WorkspaceRoot, Request.WorkspaceName);
        await new GitCliWorkspaceGit().CreateWorktreeAsync(fixture.Source, path, fixture.Commit, CancellationToken.None);
        File.WriteAllText(Path.Combine(path, "untracked.txt"), "legacy work");
        SqliteWorkspaceSessionJournal journal = fixture.Journal();
        WorkspaceSession pr = await journal.GetOrCreateAsync("github:owner/repo", 17, fixture.Source,
            Path.Combine(fixture.Root, "pr-workspaces", "pr-17"), CancellationToken.None);
        await journal.PinCommitAsync(pr.OperationId, fixture.Commit, CancellationToken.None);
        await journal.ActivateAsync(pr.OperationId, CancellationToken.None);
        Guid snapshotId = Guid.NewGuid();
        await journal.RecordSnapshotAsync(new(snapshotId, pr.OperationId, Path.Combine(fixture.Root, "old-snapshot"), 1, 5), CancellationToken.None);
        using (SqliteConnection connection = new($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
                DROP TABLE git_branch_subjects;
                PRAGMA user_version = 8;
                CREATE TABLE git_branch_sessions(operation_id TEXT PRIMARY KEY, clone_url TEXT, branch TEXT,
                    source_path TEXT, workspace_path TEXT, state TEXT, pinned_commit TEXT);
                INSERT INTO git_branch_sessions VALUES ($id, $url, $branch, $source, $path, 'Active', $commit);
                """;
            seed.Parameters.AddWithValue("$id", legacyId.ToString("D"));
            seed.Parameters.AddWithValue("$url", CloneUrl);
            seed.Parameters.AddWithValue("$branch", Request.Branch);
            seed.Parameters.AddWithValue("$source", fixture.Source);
            seed.Parameters.AddWithValue("$path", path);
            seed.Parameters.AddWithValue("$commit", fixture.Commit);
            seed.ExecuteNonQuery();
        }
        GitBranchSession migrated = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        Assert.Equal(legacyId, migrated.OperationId);
        Assert.Equal(path, migrated.WorkspacePath);
        Assert.Equal(WorkspaceSessionState.Active, migrated.State);
        Assert.True((await fixture.Handler().HandleAsync(Request, CancellationToken.None)).Reused);
        Assert.Equal("legacy work", File.ReadAllText(Path.Combine(path, "untracked.txt")));
        Assert.NotNull(await fixture.Journal().GetSnapshotAsync(snapshotId, CancellationToken.None));
        Assert.NotNull(await fixture.Slots().FindHeldSlotAsync(legacyId, CancellationToken.None));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.Journal().FindAsync(legacyId, CancellationToken.None))!.State);
        using SqliteConnection check = new($"Data Source={fixture.Database};Pooling=False");
        check.Open();
        using SqliteCommand version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(9L, version.ExecuteScalar());
        version.CommandText = "PRAGMA foreign_key_check";
        using SqliteDataReader reader = version.ExecuteReader();
        Assert.False(reader.Read());
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReusePlanPrefersExistingOwnerWithoutFetchingOrSelectingAnySlot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "tracked.txt"), "preserve user work");
        await Fixture.GitAsync(fixture.Source, "remote", "set-url", "origin", Path.Combine(fixture.Root, "missing.git"));
        GitBranchReusePlan plan = await fixture.Planner().PlanAsync(Request, CancellationToken.None);
        Assert.True(plan.ExistingOwner);
        Assert.Null(plan.TargetCommit);
        Assert.Empty(plan.Candidates);
        Assert.Equal("preserve user work", File.ReadAllText(Path.Combine(opened.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReusePreviewIsReadOnlyAndCheckoutUsesTheConfirmedCommitAfterBranchAdvance()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        OpenPullRequestResult first = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession donor = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tracked.txt"), "donor work");
        string parked = await fixture.Parking().ParkAsync(donor.OperationId, CancellationToken.None);
        WorkspaceSlot slot = await fixture.ReleaseAsync(donor.OperationId);
        GitBranchRequest target = new(CloneUrl, "other");
        GitBranchReusePlan plan = await fixture.Planner().PlanAsync(target, CancellationToken.None);
        VerifiedReuseCandidate candidate = Assert.Single(plan.Candidates);
        Assert.Equal(slot.SlotId, candidate.SlotId);
        Assert.Equal(0, candidate.ChangedPaths);
        Assert.False(candidate.CanCopyNpm);
        Assert.Equal(WorkspaceSlotState.Ready, (await fixture.Slots().FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
        Assert.Null(await fixture.Store().FindAsync(target, CancellationToken.None));
        Assert.Equal("donor work", File.ReadAllText(Path.Combine(parked, "tracked.txt")));
        File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "advanced remote");
        await Fixture.GitAsync(fixture.Source, "add", "tracked.txt");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "advance after preview");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        OpenPullRequestResult opened = await fixture.Handler().HandleInReadySlotAsync(target,
            candidate.SlotId, candidate.Generation, CancellationToken.None, plan.TargetCommit);
        Assert.Equal(plan.TargetCommit, await Fixture.GitAsync(opened.WorkspacePath, "rev-parse", "HEAD"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(opened.WorkspacePath, "tracked.txt")));
        Assert.Equal("donor work", File.ReadAllText(Path.Combine(parked, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReuseCollectorRejectsAnOccupiedReadyPathWithoutTouchingIt()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/other");
        await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession donor = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        await fixture.Parking().ParkAsync(donor.OperationId, CancellationToken.None);
        WorkspaceSlot slot = await fixture.ReleaseAsync(donor.OperationId);
        Directory.CreateDirectory(slot.WorkspacePath);
        File.WriteAllText(Path.Combine(slot.WorkspacePath, "foreign.txt"), "keep unknown data");
        GitBranchReusePlan plan = await fixture.Planner().PlanAsync(new(CloneUrl, "other"), CancellationToken.None);
        Assert.Empty(plan.Candidates);
        Assert.Equal("keep unknown data", File.ReadAllText(Path.Combine(slot.WorkspacePath, "foreign.txt")));
        Assert.Equal(WorkspaceSlotState.Ready, (await fixture.Slots().FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task BaselineFetchChangesOnlyItsDedicatedRefNotSourceEditsOrWorktrees()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "source user edit");
        string before = await Fixture.GitAsync(fixture.Source, "status", "--porcelain", "--ignored");
        string worktrees = await Fixture.GitAsync(fixture.Source, "worktree", "list", "--porcelain");
        string commit = await new GitCliWorkspaceGit().FetchBaselineCommitAsync(fixture.Source, Request.Branch, CancellationToken.None);
        Assert.Equal(fixture.Commit, commit);
        Assert.Equal(before, await Fixture.GitAsync(fixture.Source, "status", "--porcelain", "--ignored"));
        Assert.Equal(worktrees, await Fixture.GitAsync(fixture.Source, "worktree", "list", "--porcelain"));
        Assert.Contains(commit, await Fixture.GitAsync(fixture.Source, "for-each-ref", "--format=%(objectname)", "refs/git-workspace-pool/baselines/"));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task BranchFetchCannotRewriteConfiguredRemoteTrackingRefs()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "remote advance");
        await Fixture.GitAsync(fixture.Source, "add", "tracked.txt");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "advance");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work");
        string latest = await Fixture.GitAsync(fixture.Source, "rev-parse", "HEAD");
        await Fixture.GitAsync(fixture.Source, "update-ref", "refs/remotes/origin/feature/work", fixture.Commit);
        string fetched = await new GitCliWorkspaceGit().FetchBranchCommitAsync(fixture.Source,
            Request.Branch, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(latest, fetched);
        Assert.Equal(fixture.Commit, await Fixture.GitAsync(fixture.Source, "rev-parse", "refs/remotes/origin/feature/work"));
        Assert.Equal(latest, await Fixture.GitAsync(fixture.Source, "rev-parse", "HEAD"));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task SnapshotPinsLaterUserCommitSoOrdinaryGitPruningCannotBreakRestore()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession owner = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "tracked.txt"), "later user commit");
        await Fixture.GitAsync(opened.WorkspacePath, "add", "tracked.txt");
        await Fixture.GitAsync(opened.WorkspacePath, "commit", "-m", "user commit after allocation");
        string captured = await Fixture.GitAsync(opened.WorkspacePath, "rev-parse", "HEAD");
        SqliteWorkspaceSessionJournal journal = fixture.Journal();
        WorkspaceSnapshotReceipt snapshot = await new CaptureWorkspaceSnapshotService(journal, fixture.Locks(),
            new GitCliWorkspaceGit(), new VerifiedWorkspaceSnapshotExporter(Path.Combine(fixture.Root, "snapshots"),
                new GitWorktreeSnapshotScanner())).CaptureAsync(owner.OperationId, CancellationToken.None);
        // Deliberate destructive Git commands apply only to this disposable fixture, simulating later user history rewriting.
        await Fixture.GitAsync(opened.WorkspacePath, "reset", "--hard", fixture.Commit);
        await Fixture.GitAsync(fixture.Source, "reflog", "expire", "--expire=now", "--all");
        await Fixture.GitAsync(fixture.Source, "prune", "--expire=now");
        Assert.Equal("commit", await Fixture.GitAsync(fixture.Source, "cat-file", "-t", captured));
        string recovered = await new RestoreWorkspaceSnapshotService(journal, new VerifiedWorkspaceSnapshotRestorer(
            Path.Combine(fixture.Root, "recovered"), new GitCliWorkspaceGit(), new GitWorktreeSnapshotScanner(), journal))
            .RestoreAsync(snapshot.SnapshotId, CancellationToken.None);
        Assert.Equal(captured, await Fixture.GitAsync(recovered, "rev-parse", "HEAD"));
        Assert.Equal("later user commit", File.ReadAllText(Path.Combine(recovered, "tracked.txt")));
    }

    [Theory]
    [InlineData("untrusted", true)]
    [InlineData("lfs", false)]
    [Trait("Category", "FastE2E")]
    public async Task SnapshotStatusRejectsOrDisablesExecutableFiltersBeforeRunningThem(string driver, bool reject)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        string marker = Path.Combine(fixture.Root, "filter-ran").Replace('\\', '/');
        string quoted = "'" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        await Fixture.GitAsync(fixture.Source, "config", "filter." + driver + ".clean", "printf unsafe > " + quoted + "; cat");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, ".gitattributes"), "tracked.txt filter=" + driver + "\n");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "tracked.txt"), "keep this user content");
        if (reject)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitWorktreeSnapshotScanner().InspectAsync(opened.WorkspacePath, CancellationToken.None));
            Assert.Contains("external filter driver", error.Message);
        }
        else await new GitWorktreeSnapshotScanner().InspectAsync(opened.WorkspacePath, CancellationToken.None);
        Assert.False(File.Exists(marker));
        Assert.Equal("keep this user content", File.ReadAllText(Path.Combine(opened.WorkspacePath, "tracked.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReuseCandidateReportsCompatibleNpmCacheWithoutCopyingAnyFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Source, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(fixture.Source, "package-lock.json"), "{\"lockfileVersion\":3}");
        File.AppendAllText(Path.Combine(fixture.Source, ".gitignore"), "node_modules/\n");
        await Fixture.GitAsync(fixture.Source, "add", ".");
        await Fixture.GitAsync(fixture.Source, "commit", "-m", "npm manifests for preview");
        await Fixture.GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work", "HEAD:refs/heads/other");
        OpenPullRequestResult opened = await fixture.Handler().HandleAsync(Request, CancellationToken.None);
        GitBranchSession donor = (await fixture.Store().FindAsync(Request, CancellationToken.None))!;
        Directory.CreateDirectory(Path.Combine(opened.WorkspacePath, "node_modules", "fixture"));
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "node_modules", "fixture", "index.js"), "cache data");
        string parked = await fixture.Parking().ParkAsync(donor.OperationId, CancellationToken.None);
        WorkspaceSlot slot = await fixture.ReleaseAsync(donor.OperationId);
        GitBranchReusePlan plan = await fixture.Planner().PlanAsync(new(CloneUrl, "other"), CancellationToken.None);
        VerifiedReuseCandidate candidate = Assert.Single(plan.Candidates);
        Assert.True(candidate.CanCopyNpm);
        Assert.Equal(10, candidate.NpmBytes);
        Assert.False(Directory.Exists(slot.WorkspacePath));
        Assert.Equal("cache data", File.ReadAllText(Path.Combine(parked, "node_modules", "fixture", "index.js")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gwp-branch-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public string Database => Path.Combine(Root, "state", "sessions.db");
        public string Commit { get; private set; } = "";
        public Editor Editor { get; } = new();
        public GitRepositoryRegistration Repository => new(CloneUrl, Source, Path.Combine(Root, "workspaces"));

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.Root);
            string remote = Path.Combine(fixture.Root, "remote.git");
            await GitAsync(fixture.Root, "init", "--bare", "--initial-branch=main", remote);
            await GitAsync(fixture.Root, "clone", remote, fixture.Source);
            await GitAsync(fixture.Source, "config", "user.name", "Fixture");
            await GitAsync(fixture.Source, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(fixture.Source, "tracked.txt"), "original");
            File.WriteAllText(Path.Combine(fixture.Source, ".gitignore"), "ignored.bin\n");
            await GitAsync(fixture.Source, "add", ".");
            await GitAsync(fixture.Source, "commit", "-m", "initial");
            await GitAsync(fixture.Source, "push", "origin", "HEAD:refs/heads/feature/work");
            fixture.Commit = await GitAsync(fixture.Source, "rev-parse", "HEAD");
            // Initialize the existing PR schema too: branch ownership must coexist with it.
            _ = new SqliteWorkspaceSessionJournal(fixture.Database);
            _ = fixture.Store();
            return fixture;
        }

        public PlanGitBranchReuseService Planner()
        {
            GitCliWorkspaceGit git = new();
            return new PlanGitBranchReuseService(new Catalog(Repository), Store(), new RemoteReader(), git,
                new VerifiedReuseCandidateCollector(Slots(), Journal(), git, new GitWorkspaceParkingOperations(git), new WorkspaceReadySlotSafety()));
        }

        public SqliteWorkspaceSessionJournal Journal() => new(Database);
        public SqliteWorkspaceSlotRegistry Slots() => new(Database);
        public FileWorkspaceOperationLock Locks() => new(Path.Combine(Root, "locks"));
        public ParkWorkspaceSessionService Parking() => new(Journal(), Journal(), Locks(),
            new GitWorkspaceParkingOperations(new GitCliWorkspaceGit()));

        public async Task<WorkspaceSlot> ReleaseAsync(Guid sessionId)
        {
            AssessWorkspaceReleaseService assess = new(Journal(), Slots(), Locks(),
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit()));
            WorkspaceSlotReleasePreparation preparation = await new PrepareWorkspaceSlotReleaseService(
                Journal(), Locks(), assess, Slots()).PrepareAsync(sessionId, CancellationToken.None);
            await new FinalizeWorkspaceSlotReleaseService(Journal(), Locks(), assess, Slots())
                .FinalizeAsync(preparation.PreparationId, CancellationToken.None);
            return (await Slots().FindSlotAsync(preparation.SlotId, CancellationToken.None))!;
        }

        public SqliteGitBranchSessionStore Store() => new(Database);

        public Task<GitBranchSession> ReserveAsync(GitBranchRequest request) =>
            Store().ReserveAsync(request, Repository, Path.Combine(Repository.WorkspaceRoot, request.WorkspaceName), CancellationToken.None);

        public OpenGitBranchHandler Handler(IGitRemoteReader? remotes = null)
        {
            GitCliWorkspaceGit git = new();
            // Offline fixture replaces only remote identity, never Git fetch/checkout, SQLite or locks.
            return new(new Catalog(Repository), remotes ?? new RemoteReader(), git, git, Store(),
                new FileWorkspaceOperationLock(Path.Combine(Root, "locks")), new WorkspaceDirectories(),
                new WorkspaceReadySlotSafety(), new VerifiedReadySlotCheckout(), Editor,
                new OpenReleasedParkedWorkspaceService(Journal(), Locks(), new GitWorkspaceParkingOperations(git), Editor),
                new AcquireExplicitReadySlotService(Slots(), Slots(), new WorkspaceReadySlotSafety(), git,
                    new WorkspaceDirectories(), Journal(), Locks()));
        }

        public void Dispose()
        {
            // Fixture directories are disposable; production code never removes managed worktrees.
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }

        public static async Task<string> GitAsync(string directory, params string[] args)
        {
            ProcessStartInfo start = new("git")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.TrimEnd('\r', '\n');
        }
    }

    private sealed class Catalog(GitRepositoryRegistration repository) : IGitBranchCatalog
    {
        public GitRepositoryRegistration? Find(GitBranchRequest request) => repository;
    }

    private sealed class RemoteReader : IGitRemoteReader
    {
        public Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken) => Task.FromResult(CloneUrl);
    }

    private sealed class Editor : IEditorLauncher
    {
        public List<string> Opened { get; } = [];
        public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
        {
            lock (Opened) Opened.Add(workspacePath);
            return Task.CompletedTask;
        }
    }
}
