using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Core.Protocol;
using GitWorkspacePool.Infrastructure;
using GitWorkspacePool.Infrastructure.Snapshots;
using GitWorkspacePool.Infrastructure.Transport;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

/// <summary>
/// Fast E2E coverage: real Git and filesystem, offline PR remote, fake editor.
/// </summary>
public sealed class GitWorkspaceE2ETests
{
    private const string PullRequestUrl = "https://github.com/owner/repo/pull/17";

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ProtocolUrlToRealGitWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string protocolUrl = "prworkspace://open?url="
            + Uri.EscapeDataString(PullRequestUrl)
            + "&v=1";

        Assert.True(OpenRequestProtocol.TryExtract(protocolUrl, out string requestUrl));
        OpenPullRequestResult result = await fixture.OpenAsync(requestUrl);

        Assert.True(await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath));
        Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_NamedPipeToRealGitAndFakeEditor()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<OpenPullRequestResult> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        string pipeName = "GitWorkspacePool.GitE2E." + Guid.NewGuid().ToString("N");
        string protocolUrl = "prworkspace://open?url="
            + Uri.EscapeDataString(PullRequestUrl)
            + "&v=1";

        NamedPipeRequestServer server = new(pipeName);
        Task listener = server.ListenAsync(
            message => _ = DispatchAsync(message),
            shutdown.Token
        );

        async Task DispatchAsync(string message)
        {
            try
            {
                if (!OpenRequestProtocol.TryExtract(message, out string requestUrl))
                {
                    throw new InvalidOperationException("Rejected protocol URL.");
                }

                completed.TrySetResult(await fixture.OpenAsync(requestUrl));
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
        }

        try
        {
            await new NamedPipeRequestClient().SendAsync(
                pipeName,
                protocolUrl,
                shutdown.Token
            );
            OpenPullRequestResult result = await completed.Task.WaitAsync(shutdown.Token);

            Assert.False(result.Reused);
            Assert.True(await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath));
            Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
        }
        finally
        {
            await shutdown.CancelAsync();
            await listener;
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_CreatesDetachedWorktreeFromLocalPullRequestRef()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult result = await fixture.OpenAsync(PullRequestUrl);

        Assert.False(result.Reused);
        Assert.Equal("feature for pr 17", File.ReadAllText(Path.Combine(result.WorkspacePath, "feature.txt")));
        Assert.Equal(result.WorkspacePath, Assert.Single(fixture.Editor.OpenedPaths));
        Assert.True(
            await fixture.IsRegisteredWorktreeAsync(result.WorkspacePath),
            "The path must be present in the source clone's Git worktree registry."
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task GitCli_DisablesConfiguredPostCheckoutHookDuringPrOpen()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureFailingCheckoutHookAsync();

        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);

        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
        Assert.Equal(
            "feature for pr 17",
            File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt"))
        );
    }

    [Theory]
    [InlineData("core.sshCommand", "never-run")]
    [InlineData("core.gitProxy", "never-run")]
    [InlineData("remote.origin.vcs", "never-run")]
    [InlineData("remote.origin.uploadpack", "never-run")]
    [InlineData("remote.origin.receivepack", "never-run")]
    [InlineData("url.https://mirror.example/repo/.insteadOf", "https://github.com/")]
    public async Task GitCli_RejectsUnsafeFetchConfigBeforeCheckout(
        string key,
        string value
    )
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GitInDirectoryAsync(
            fixture.SourcePath, "config", "--local", key, value
        );

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Contains("unsafe URL rewrite or external Git transport", error.Message);
        Assert.False(Directory.Exists(fixture.WorkspacePath(17)));
        Assert.False(await fixture.IsRegisteredWorktreeAsync(fixture.WorkspacePath(17)));
        Assert.Empty(fixture.Editor.OpenedPaths);

        // The same effective-configuration preflight protects origin resolution
        // in non-fixture GitHub origin verification.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitCliWorkspaceGit().GetOriginUrlAsync(
                fixture.SourcePath, CancellationToken.None
            )
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task GitCli_RejectsConfigIntroducedAfterOriginPreflight()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitCliWorkspaceGit git = new();
        await git.GetOriginUrlAsync(fixture.SourcePath, CancellationToken.None);
        await fixture.GitInDirectoryAsync(
            fixture.SourcePath, "config", "--local",
            "url.https://mirror.example/.insteadOf", "https://github.com/"
        );

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => git.FetchPullRequestAsync(
                fixture.SourcePath, 17, CancellationToken.None
            )
        );
        Assert.Contains("unsafe URL rewrite or external Git transport", error.Message);
        Assert.False(Directory.Exists(fixture.WorkspacePath(17)));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task GitCli_RefusesCustomSmudgeDriverBeforeCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigureDangerousFilterAsync();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Contains("external filter driver", exception.Message);
        Assert.False(Directory.Exists(fixture.WorkspacePath(17)));
        Assert.False(await fixture.IsRegisteredWorktreeAsync(fixture.WorkspacePath(17)));
        Assert.Empty(fixture.Editor.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_RefusesCustomSmudgeDriverBeforeCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "preserve original");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();

        await fixture.ConfigureDangerousFilterAsync();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(snapshot.SnapshotId)
        );

        Assert.Contains("external filter driver", exception.Message);
        Assert.Equal("preserve original", File.ReadAllText(changed));
        Assert.Empty(Directory.Exists(fixture.RecoveryRoot)
            ? Directory.GetDirectories(fixture.RecoveryRoot)
            : []);
        WorkspaceRestoreOperation intent = Assert.Single(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, intent.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ReopensWithoutOverwritingLocalChanges()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        string changedPath = Path.Combine(first.WorkspacePath, "feature.txt");
        string untrackedPath = Path.Combine(first.WorkspacePath, "notes.local");
        string binaryPath = Path.Combine(first.WorkspacePath, "image.local");
        byte[] binary = [0, 255, 19, 0, 42, 128];
        File.WriteAllText(changedPath, "user has unsaved work");
        File.WriteAllText(untrackedPath, "never delete me");
        File.WriteAllBytes(binaryPath, binary);

        OpenPullRequestResult second = await fixture.OpenAsync(PullRequestUrl);

        Assert.True(second.Reused);
        Assert.Equal(first.WorkspacePath, second.WorkspacePath);
        Assert.Equal("user has unsaved work", File.ReadAllText(changedPath));
        Assert.Equal("never delete me", File.ReadAllText(untrackedPath));
        Assert.Equal(binary, File.ReadAllBytes(binaryPath));
        Assert.Equal(2, fixture.Editor.OpenedPaths.Count);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Session_SuspendAndResumeRetainTrackedStagedUntrackedAndIgnoredFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();

        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "staged user content");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(changed, "unstaged user content");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "untracked");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(opened.WorkspacePath, "generated"));
        File.WriteAllBytes(Path.Combine(opened.WorkspacePath, "generated", "cache.bin"), [0, 19, 255]);

        string path = await fixture.SuspendAsync(owner.OperationId);
        Assert.Equal(opened.WorkspacePath, path);
        Assert.Equal(
            WorkspaceSessionState.Suspended,
            (await fixture.SessionAsync(owner.OperationId)).State
        );
        Assert.Empty(await fixture.ActiveSessionsAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SuspendAsync(owner.OperationId)
        );

        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);
        Assert.True(reopened.Reused);
        Assert.Equal(opened.WorkspacePath, reopened.WorkspacePath);
        Assert.Equal(
            WorkspaceSessionState.Active,
            (await fixture.SessionAsync(owner.OperationId)).State
        );
        Assert.Equal("staged user content", await fixture.ReadStagedFeatureAsync());
        Assert.Equal("unstaged user content", File.ReadAllText(changed));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(path, "notes.local")));
        Assert.Equal(
            new byte[] { 0, 19, 255 },
            File.ReadAllBytes(Path.Combine(path, "generated", "cache.bin"))
        );
        Assert.True(await fixture.IsRegisteredWorktreeAsync(path));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Session_RecoveryRequiredNeverReactivatesAutomatically()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        await journal.RequireRecoveryAsync(owner.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync(PullRequestUrl)
        );

        Assert.Equal(
            WorkspaceSessionState.RecoveryRequired,
            (await fixture.SessionAsync(owner.OperationId)).State
        );
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
        Assert.Single(fixture.Editor.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Parking_RoundTripPreservesStagedUnstagedIgnoredAndUntrackedFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession session = await fixture.OwnedSessionAsync();
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "stage one");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(changed, "working two");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(opened.WorkspacePath, "generated"));
        File.WriteAllBytes(Path.Combine(opened.WorkspacePath, "generated", "ignored.bin"), [0, 255, 19]);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "keep this too");

        string parked = await fixture.ParkAsync(session.OperationId);

        Assert.False(Directory.Exists(opened.WorkspacePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.False(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
        Assert.Equal(WorkspaceSessionState.Parked, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.Equal("working two", File.ReadAllText(Path.Combine(parked, "feature.txt")));
        Assert.Equal("keep this too", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.Equal(new byte[] { 0, 255, 19 },
            File.ReadAllBytes(Path.Combine(parked, "generated", "ignored.bin")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync(PullRequestUrl)
        );

        string restored = await fixture.UnparkAsync(session.OperationId);
        Assert.Equal(opened.WorkspacePath, restored);
        Assert.False(Directory.Exists(parked));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(restored));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.Equal("stage one", await fixture.ReadStagedFeatureAsync());
        Assert.Equal("working two", File.ReadAllText(changed));
        Assert.Equal("keep this too", File.ReadAllText(Path.Combine(restored, "notes.local")));
        Assert.Equal(new byte[] { 0, 255, 19 },
            File.ReadAllBytes(Path.Combine(restored, "generated", "ignored.bin")));
        Assert.True((await fixture.OpenWithFreshHandlerAsync(PullRequestUrl)).Reused);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Parking_InterruptedJournalAndMoveCanBeSafelyRetried()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession session = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "do not lose");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ParkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.ParkingIntentRecorded)
            )
        );
        Assert.Equal(WorkspaceSessionState.Parking, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.True(Directory.Exists(opened.WorkspacePath));

        string parked = await fixture.ParkAsync(session.OperationId);
        Assert.Equal("do not lose", File.ReadAllText(Path.Combine(parked, "notes.local")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.UnparkedWorktreeMoved)
            )
        );
        Assert.Equal(WorkspaceSessionState.Unparking, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.True(Directory.Exists(opened.WorkspacePath));
        Assert.False(Directory.Exists(parked));

        string returned = await fixture.UnparkAsync(session.OperationId);
        Assert.Equal(opened.WorkspacePath, returned);
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.Equal("do not lose", File.ReadAllText(Path.Combine(returned, "notes.local")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ParkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.ParkedWorktreeMoved)
            )
        );
        Assert.Equal(WorkspaceSessionState.Parking, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.False(Directory.Exists(returned));
        Assert.Equal("do not lose",
            File.ReadAllText(Path.Combine((await fixture.SessionAsync(session.OperationId))!.ParkingPath!, "notes.local")));
        await fixture.ParkAsync(session.OperationId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.UnparkingIntentRecorded)
            )
        );
        Assert.Equal(WorkspaceSessionState.Unparking, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.True(Directory.Exists(parked));
        Assert.Equal(returned, await fixture.UnparkAsync(session.OperationId));
        Assert.Equal("do not lose", File.ReadAllText(Path.Combine(returned, "notes.local")));
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task Parking_RealProcessKillAtAllMoveBoundariesRetainsUserData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string userFile = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(userFile, "must survive real crashes");

        // Crash after SQLite intent but before Git has touched either worktree.
        _ = await SubprocessCrashTests.KillAtParkingCheckpointAsync(
            "park",
            fixture.DatabasePath,
            owner.OperationId,
            fixture.SignalRoot,
            WorkspaceParkingCheckpoint.ParkingIntentRecorded,
            fixture.SignalRoot
        );
        Assert.Equal(
            WorkspaceSessionState.Parking,
            (await fixture.SessionAsync(owner.OperationId))!.State
        );
        Assert.Equal("must survive real crashes", File.ReadAllText(userFile));

        string parked = await fixture.ParkAsync(owner.OperationId);
        Assert.Equal("must survive real crashes", File.ReadAllText(Path.Combine(parked, "notes.local")));

        // Crash after returning the worktree but before the journal's Active transition.
        _ = await SubprocessCrashTests.KillAtParkingCheckpointAsync(
            "unpark",
            fixture.DatabasePath,
            owner.OperationId,
            fixture.SignalRoot,
            WorkspaceParkingCheckpoint.UnparkedWorktreeMoved,
            fixture.SignalRoot
        );
        Assert.Equal(
            WorkspaceSessionState.Unparking,
            (await fixture.SessionAsync(owner.OperationId))!.State
        );
        Assert.True(Directory.Exists(opened.WorkspacePath));
        Assert.Equal("must survive real crashes", File.ReadAllText(userFile));
        Assert.Equal(opened.WorkspacePath, await fixture.UnparkAsync(owner.OperationId));

        // Crash after Git moved the worktree into parking but before DB commit.
        _ = await SubprocessCrashTests.KillAtParkingCheckpointAsync(
            "park",
            fixture.DatabasePath,
            owner.OperationId,
            fixture.SignalRoot,
            WorkspaceParkingCheckpoint.ParkedWorktreeMoved,
            fixture.SignalRoot
        );
        Assert.Equal(
            WorkspaceSessionState.Parking,
            (await fixture.SessionAsync(owner.OperationId))!.State
        );
        Assert.False(Directory.Exists(opened.WorkspacePath));
        Assert.Equal("must survive real crashes", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.Equal(parked, await fixture.ParkAsync(owner.OperationId));

        // Crash after the reverse intent but before Git moved anything.
        _ = await SubprocessCrashTests.KillAtParkingCheckpointAsync(
            "unpark",
            fixture.DatabasePath,
            owner.OperationId,
            fixture.SignalRoot,
            WorkspaceParkingCheckpoint.UnparkingIntentRecorded,
            fixture.SignalRoot
        );
        Assert.Equal(
            WorkspaceSessionState.Unparking,
            (await fixture.SessionAsync(owner.OperationId))!.State
        );
        Assert.Equal("must survive real crashes", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.Equal(opened.WorkspacePath, await fixture.UnparkAsync(owner.OperationId));
        Assert.Equal("must survive real crashes", File.ReadAllText(userFile));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Parking_RecoveryInventoryShowsInterruptedMovesAndMissingParkedTree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession session = await fixture.OwnedSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ParkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.ParkingIntentRecorded)
            )
        );

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.InterruptedParking
        );

        string parked = await fixture.ParkAsync(session.OperationId);
        Assert.DoesNotContain(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind is WorkspaceRecoveryKind.InterruptedParking
                or WorkspaceRecoveryKind.MissingParkedWorktree
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(
                session.OperationId,
                new ThrowAtParkingCheckpoint(WorkspaceParkingCheckpoint.UnparkingIntentRecorded)
            )
        );

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.InterruptedUnparking
                && finding.Path == parked
        );

        string returned = await fixture.UnparkAsync(session.OperationId);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(returned));
        Assert.DoesNotContain(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind is WorkspaceRecoveryKind.InterruptedParking
                or WorkspaceRecoveryKind.InterruptedUnparking
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Parking_SubmoduleIndexEntryIsRefusedBeforeMoveIntent()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession session = await fixture.OwnedSessionAsync();
        string head = await new GitCliWorkspaceGit().GetWorktreeCommitAsync(
            opened.WorkspacePath, CancellationToken.None
        );

        await fixture.GitInWorkspaceAsync(
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{head},external/submodule"
        );
        string note = Path.Combine(opened.WorkspacePath, "local.txt");
        File.WriteAllText(note, "important user data");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ParkAsync(session.OperationId)
        );

        Assert.Contains("submodule", failure.Message);
        Assert.Equal("important user data", File.ReadAllText(note));
        Assert.Equal(
            WorkspaceSessionState.Active,
            (await fixture.SessionAsync(session.OperationId))!.State
        );
        Assert.Null((await fixture.SessionAsync(session.OperationId))!.ParkingPath);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasePreflight_RejectsActiveAndApprovesOnlyVerifiedParkedOwner()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "keep this");

        WorkspaceReleaseAssessment active = await fixture.AssessReleaseAsync(owner.OperationId);
        Assert.False(active.CanBeginRelease);
        Assert.Equal(WorkspaceReleaseBlocker.SessionNotParked, active.Blocker);
        Assert.NotNull(active.SlotId);
        Assert.Equal(0L, active.Generation);

        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceReleaseAssessment eligible = await fixture.AssessReleaseAsync(owner.OperationId);
        Assert.True(eligible.CanBeginRelease);
        Assert.Equal(active.SlotId, eligible.SlotId);
        Assert.Equal(active.Generation, eligible.Generation);
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(parked, "notes.local")));

        // Assessment never commits Ready, removes ownership, or deletes anything.
        WorkspaceSlot protectedSlot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath).FindHeldSlotAsync(
                owner.OperationId, CancellationToken.None
            )
        );
        Assert.Equal(WorkspaceSlotState.Protected, protectedSlot.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));

        string returned = await fixture.UnparkAsync(owner.OperationId);
        Assert.Equal(opened.WorkspacePath, returned);
        Assert.Equal(
            WorkspaceReleaseBlocker.SessionNotParked,
            (await fixture.AssessReleaseAsync(owner.OperationId)).Blocker
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasePreparation_JournalSurvivesRestartAndUnparkRequiresCancellation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "unique local data");
        string parked = await fixture.ParkAsync(owner.OperationId);

        WorkspaceSlotReleasePreparation preparation = await fixture.PrepareReleaseAsync(
            owner.OperationId
        );
        Assert.Equal(WorkspaceSlotReleasePreparationState.Prepared, preparation.State);

        SqliteWorkspaceSlotRegistry reopened = new(fixture.DatabasePath);
        Assert.Equal(
            preparation,
            Assert.Single(await reopened.ListPreparedAsync(CancellationToken.None))
        );
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotReleasePreparation
                && finding.Path == opened.WorkspacePath
                && finding.Description.Contains(preparation.PreparationId.ToString("N"))
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(owner.OperationId)
        );

        // A prepared release has no permission to reuse the original path.
        Assert.Equal(
            "unique local data",
            File.ReadAllText(Path.Combine(parked, "notes.local"))
        );
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal(WorkspaceSessionState.Parked,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await reopened.FindHeldSlotAsync(owner.OperationId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, slot.State);

        Assert.True(await reopened.CancelAsync(preparation.PreparationId, CancellationToken.None));
        Assert.Empty(await reopened.ListPreparedAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotReleasePreparation
        );
        Assert.Equal(opened.WorkspacePath, await fixture.UnparkAsync(owner.OperationId));
        Assert.Equal(
            "unique local data",
            File.ReadAllText(Path.Combine(opened.WorkspacePath, "notes.local"))
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleaseCommit_PreservesParkedUserDataAndFreesOriginalSlot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "staged");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "unstaged");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "precious data");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation committed = await fixture.FinalizeReleaseAsync(
            prepared.PreparationId
        );

        Assert.Equal(WorkspaceSlotReleasePreparationState.Committed, committed.State);
        Assert.Equal(committed, await fixture.FinalizeReleaseAsync(prepared.PreparationId));
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        Assert.False(Directory.Exists(opened.WorkspacePath));
        Assert.False(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(parked, "feature.txt")));
        Assert.Equal("precious data", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.Equal("staged", await fixture.ReadStagedFeatureAsync(parked));
        Assert.NotNull(await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
            .GetSnapshotAsync(receipt.SnapshotId, CancellationToken.None));
        string recovered = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(recovered, "feature.txt")));
        Assert.Equal("precious data", File.ReadAllText(Path.Combine(recovered, "notes.local")));
        Assert.Equal("staged", await fixture.ReadStagedFeatureAsync(recovered));

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        Assert.Null(await registry.FindHeldSlotAsync(owner.OperationId, CancellationToken.None));
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Ready, slot.State);
        Assert.Equal(prepared.Generation, slot.Generation);
        Assert.Empty(await registry.ListPreparedAsync(CancellationToken.None));
        Assert.Empty(await registry.AuditAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(owner.OperationId)
        );
        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);
        Assert.True(reopened.Reused);
        Assert.Equal(parked, reopened.WorkspacePath);
        Assert.Equal("precious data", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.DoesNotContain(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.MissingParkedWorktree
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_ExplicitSecondPrCheckoutRetainsFirstPrParkedData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession firstOwner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, "feature.txt"), "stage protected");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "feature.txt"), "unstaged protected");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "private.local"), "secret user data");
        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        string parked = await fixture.ParkAsync(firstOwner.OperationId);
        WorkspaceSlotReleasePreparation preparation = await fixture.PrepareReleaseAsync(
            firstOwner.OperationId
        );
        _ = await fixture.FinalizeReleaseAsync(preparation.PreparationId);

        await fixture.CreatePullRequestAsync(18);
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(preparation.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Ready, slot.State);
        Assert.Contains(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .ListReadyAsync(CancellationToken.None),
            candidate => candidate.SlotId == slot.SlotId
                && candidate.Generation == slot.Generation
        );

        OpenPullRequestResult second = await fixture.OpenInReadySlotWithHandlerAsync(
            "https://github.com/owner/repo/pull/18",
            slot.SlotId,
            slot.Generation
        );
        Assert.Equal(first.WorkspacePath, second.WorkspacePath);
        Assert.Contains(second.WorkspacePath, fixture.Editor.OpenedPaths);
        Assert.False(second.Reused);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(second.WorkspacePath));
        Assert.Equal("feature for pr 18", File.ReadAllText(
            Path.Combine(second.WorkspacePath, "feature.txt")
        ));
        Assert.False(File.Exists(Path.Combine(second.WorkspacePath, "private.local")));
        Assert.Equal("unstaged protected", File.ReadAllText(
            Path.Combine(parked, "feature.txt")
        ));
        Assert.Equal("secret user data", File.ReadAllText(
            Path.Combine(parked, "private.local")
        ));
        Assert.Equal("stage protected", await fixture.ReadStagedFeatureAsync(parked));
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(firstOwner.OperationId))!.State);
        WorkspaceSnapshotRecord historical = Assert.IsType<WorkspaceSnapshotRecord>(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .GetSnapshotAsync(saved.SnapshotId, CancellationToken.None)
        );
        Assert.Equal(firstOwner.OperationId, historical.Session.OperationId);

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceSlot reused = Assert.IsType<WorkspaceSlot>(
            await registry.FindSlotAsync(slot.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, reused.State);
        Assert.Equal(slot.Generation + 1, reused.Generation);
        Assert.Empty(await registry.ListReadyAsync(CancellationToken.None));
        Assert.Empty(await registry.AuditAsync(CancellationToken.None));

        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(
            "https://github.com/owner/repo/pull/18"
        );
        Assert.True(reopened.Reused);
        Assert.Equal(second.WorkspacePath, reopened.WorkspacePath);
        Assert.Equal("secret user data", File.ReadAllText(
            Path.Combine(parked, "private.local")
        ));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasedPR_ReopenRetainedParkingAfterSecondPrClaimsFormerSlot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession original = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, "feature.txt"), "staged old PR");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "feature.txt"), "unstaged old PR");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "private.local"), "old owner data");

        string parked = await fixture.ParkAsync(original.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(
            original.OperationId
        );
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);
        OpenPullRequestResult second = await fixture.OpenInReadySlotAsync(
            "https://github.com/owner/repo/pull/18",
            prepared.SlotId,
            prepared.Generation
        );
        Assert.Equal(first.WorkspacePath, second.WorkspacePath);

        int before = fixture.Editor.OpenedPaths.Count;
        Assert.Equal(parked, await fixture.OpenRetainedReleasedAsync(original.OperationId));
        Assert.Equal(before + 1, fixture.Editor.OpenedPaths.Count);
        Assert.Equal(parked, fixture.Editor.OpenedPaths.Last());

        // Browser URL -> trusted PR handler -> retained original PR
        // must not touch the second PR's leased former slot.
        OpenPullRequestResult fromBrowser = await fixture.OpenWithFreshHandlerAsync(
            PullRequestUrl
        );
        Assert.True(fromBrowser.Reused);
        Assert.Equal(parked, fromBrowser.WorkspacePath);
        Assert.Equal(before + 2, fixture.Editor.OpenedPaths.Count);
        Assert.Equal(parked, fixture.Editor.OpenedPaths.Last());

        Assert.Equal("unstaged old PR", File.ReadAllText(
            Path.Combine(parked, "feature.txt")
        ));
        Assert.Equal("staged old PR", await fixture.ReadStagedFeatureAsync(parked));
        Assert.Equal("old owner data", File.ReadAllText(
            Path.Combine(parked, "private.local")
        ));
        Assert.Equal("feature for pr 18", File.ReadAllText(
            Path.Combine(second.WorkspacePath, "feature.txt")
        ));
        Assert.False(File.Exists(Path.Combine(second.WorkspacePath, "private.local")));
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(original.OperationId))!.State);

        // The retained worktree is editable. A user commit must not strand
        // them: the Git registration and path still establish ownership.
        await fixture.GitInDirectoryAsync(
            parked, "-c", "user.name=E2E", "-c", "user.email=test@example.invalid",
            "commit", "--allow-empty", "-m", "more work on released PR"
        );
        Assert.Equal(parked, await fixture.OpenRetainedReleasedAsync(original.OperationId));
        Assert.Equal(parked, (await fixture.OpenWithFreshHandlerAsync(
            PullRequestUrl
        )).WorkspacePath);
        Assert.Equal("unstaged old PR", File.ReadAllText(
            Path.Combine(parked, "feature.txt")
        ));
        Assert.Equal("feature for pr 18", File.ReadAllText(
            Path.Combine(second.WorkspacePath, "feature.txt")
        ));
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(original.OperationId))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasedPR_RetainedOpenRejectsUnreleasedAndMissingGitTree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession original = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, "private.local"), "preserve me");
        string parked = await fixture.ParkAsync(original.OperationId);
        int openedBefore = fixture.Editor.OpenedPaths.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenRetainedReleasedAsync(original.OperationId)
        );
        Assert.Equal(openedBefore, fixture.Editor.OpenedPaths.Count);

        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(
            original.OperationId
        );
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        string displaced = parked + "-displaced";
        Directory.Move(parked, displaced);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenRetainedReleasedAsync(original.OperationId)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync(PullRequestUrl)
        );

        Assert.Equal(openedBefore, fixture.Editor.OpenedPaths.Count);
        Assert.Equal("preserve me", File.ReadAllText(
            Path.Combine(displaced, "private.local")
        ));
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Ready, slot.State);
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(original.OperationId))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task WarmNpm_ExplicitCopyReusesIgnoredDependenciesAndPreservesFormerPr()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession oldOwner = await fixture.OwnedSessionAsync();
        string original = first.WorkspacePath;
        File.WriteAllText(Path.Combine(original, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(original, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(original, "package-lock.json"), "{\"lockfileVersion\":3}");
        File.WriteAllText(Path.Combine(original, "notes.local"), "old PR must survive");
        string cached = Path.Combine(original, "node_modules", "fixture-package");
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "index.js"), "module.exports = 42");
        byte[] binary = Enumerable.Range(0, 4096).Select(n => (byte)(n % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(cached, "cache.bin"), binary);

        string parked = await fixture.ParkAsync(oldOwner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(oldOwner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);
        OpenPullRequestResult second = await fixture.OpenInReadySlotAsync(
            "https://github.com/owner/repo/pull/18", prepared.SlotId, prepared.Generation
        );
        WorkspaceSession newOwner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(second.WorkspacePath, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(second.WorkspacePath, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(second.WorkspacePath, "package-lock.json"), "{\"lockfileVersion\":3}");

        System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        WarmArtifactTransferReceipt receipt = await fixture.TransferNpmAsync(
            oldOwner.OperationId, newOwner.OperationId
        );
        wall.Stop();

        Assert.Equal(2, receipt.FileCount);
        Assert.Equal(binary.Length + "module.exports = 42".Length, receipt.CopiedBytes);
        Assert.Equal("node_modules", receipt.ArtifactRelativePath);
        Assert.True(receipt.Elapsed >= TimeSpan.Zero);
        Assert.True(wall.Elapsed >= receipt.Elapsed);
        Assert.Equal("module.exports = 42", File.ReadAllText(
            Path.Combine(second.WorkspacePath, "node_modules", "fixture-package", "index.js")
        ));
        Assert.Equal(binary, File.ReadAllBytes(
            Path.Combine(second.WorkspacePath, "node_modules", "fixture-package", "cache.bin")
        ));
        Assert.Equal("old PR must survive", File.ReadAllText(
            Path.Combine(parked, "notes.local")
        ));
        Assert.Equal("module.exports = 42", File.ReadAllText(
            Path.Combine(parked, "node_modules", "fixture-package", "index.js")
        ));
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(oldOwner.OperationId))!.State);
        Assert.Equal(WorkspaceSessionState.Active,
            (await fixture.SessionAsync(newOwner.OperationId))!.State);

        // The cache is immutable from the old PR's perspective and never
        // overwrites a previously imported destination on repeated actions.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.TransferNpmAsync(oldOwner.OperationId, newOwner.OperationId)
        );
        Assert.Equal(binary, File.ReadAllBytes(
            Path.Combine(second.WorkspacePath, "node_modules", "fixture-package", "cache.bin")
        ));
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task WarmNpm_BenchmarkColdCheckoutVsVerifiedArtifactCopy()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        System.Diagnostics.Stopwatch cold = System.Diagnostics.Stopwatch.StartNew();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        cold.Stop();
        WorkspaceSession original = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "package.json"), "{\"name\":\"fixture\"}");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "package-lock.json"), "{\"lockfileVersion\":3}");
        string packages = Path.Combine(first.WorkspacePath, "node_modules", "reference");
        Directory.CreateDirectory(packages);
        for (int n = 0; n < 1024; n++)
        {
            File.WriteAllBytes(Path.Combine(packages, n.ToString("D4") + ".bin"),
                Enumerable.Repeat((byte)(n % 251), 2048).ToArray());
        }

        _ = await fixture.ParkAsync(original.OperationId);
        WorkspaceSlotReleasePreparation released = await fixture.PrepareReleaseAsync(
            original.OperationId
        );
        _ = await fixture.FinalizeReleaseAsync(released.PreparationId);
        await fixture.CreatePullRequestAsync(18);
        System.Diagnostics.Stopwatch nextCheckout = System.Diagnostics.Stopwatch.StartNew();
        OpenPullRequestResult second = await fixture.OpenInReadySlotAsync(
            "https://github.com/owner/repo/pull/18",
            released.SlotId, released.Generation
        );
        nextCheckout.Stop();
        WorkspaceSession target = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(second.WorkspacePath, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(second.WorkspacePath, "package.json"), "{\"name\":\"fixture\"}");
        File.WriteAllText(Path.Combine(second.WorkspacePath, "package-lock.json"), "{\"lockfileVersion\":3}");
        WarmArtifactTransferReceipt receipt = await fixture.TransferNpmAsync(
            original.OperationId, target.OperationId
        );

        Console.WriteLine(
            "Cold PR17 initial checkout: {0:F1} ms; PR18 empty-slot checkout: {1:F1} ms; "
                + "explicit npm artifact copy: {2:F1} ms for {3} files / {4} bytes. "
                + "PR18 checkout+copy: {5:F1} ms (excludes dependency installation).",
            cold.Elapsed.TotalMilliseconds,
            nextCheckout.Elapsed.TotalMilliseconds,
            receipt.Elapsed.TotalMilliseconds,
            receipt.FileCount,
            receipt.CopiedBytes,
            nextCheckout.Elapsed.TotalMilliseconds + receipt.Elapsed.TotalMilliseconds
        );

        Assert.Equal(1024, receipt.FileCount);
        Assert.Equal(1024L * 2048, receipt.CopiedBytes);
        Assert.True(File.Exists(Path.Combine(
            second.WorkspacePath, "node_modules", "reference", "0000.bin"
        )));

        // Stable machine-readable output for the manual, repeated Windows
        // benchmark. These timings use real Git+SQLite and the production
        // verified artifact copier, but a fake editor and no npm installation.
        string resultJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            coldCheckoutMs = cold.Elapsed.TotalMilliseconds,
            releasedSlotCheckoutMs = nextCheckout.Elapsed.TotalMilliseconds,
            verifiedArtifactCopyMs = receipt.Elapsed.TotalMilliseconds,
            artifactFiles = receipt.FileCount,
            artifactLogicalBytes = receipt.CopiedBytes,
        });
        Console.WriteLine("GWP_BENCHMARK_JSON=" + resultJson);
        string? benchmarkFile = Environment.GetEnvironmentVariable("GWP_BENCHMARK_FILE");
        if (!string.IsNullOrWhiteSpace(benchmarkFile))
        {
            // Write only a never-existing file under the caller's disposable
            // benchmark output directory. Never truncate user data.
            using FileStream stream = new(
                benchmarkFile, FileMode.CreateNew, FileAccess.Write, FileShare.None
            );
            using StreamWriter writer = new(stream);
            writer.WriteLine(resultJson);
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task WarmNpm_MismatchedManifestAndForeignDestinationAreNeverOverwritten()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession oldOwner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "package.json"), "{\"name\":\"fixture\"}");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "package-lock.json"), "{\"lockfileVersion\":3}");
        Directory.CreateDirectory(Path.Combine(first.WorkspacePath, "node_modules"));
        File.WriteAllText(Path.Combine(first.WorkspacePath, "node_modules", "package.js"), "unchanged");

        string parked = await fixture.ParkAsync(oldOwner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(oldOwner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);
        OpenPullRequestResult second = await fixture.OpenInReadySlotAsync(
            "https://github.com/owner/repo/pull/18", prepared.SlotId, prepared.Generation
        );
        WorkspaceSession newOwner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(second.WorkspacePath, ".gitignore"), "node_modules/\n");
        File.WriteAllText(Path.Combine(second.WorkspacePath, "package.json"), "{\"name\":\"fixture\"}");
        string targetLock = Path.Combine(second.WorkspacePath, "package-lock.json");
        File.WriteAllText(targetLock, "{\"lockfileVersion\":2}");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.TransferNpmAsync(oldOwner.OperationId, newOwner.OperationId)
        );
        Assert.False(Directory.Exists(Path.Combine(second.WorkspacePath, "node_modules")));
        Assert.Equal("{\"lockfileVersion\":2}", File.ReadAllText(targetLock));

        File.WriteAllText(targetLock, "{\"lockfileVersion\":3}");
        string targetCache = Path.Combine(second.WorkspacePath, "node_modules");
        Directory.CreateDirectory(targetCache);
        File.WriteAllText(Path.Combine(targetCache, "foreign.txt"), "never overwrite");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.TransferNpmAsync(oldOwner.OperationId, newOwner.OperationId)
        );
        Assert.Equal("never overwrite", File.ReadAllText(
            Path.Combine(targetCache, "foreign.txt")
        ));
        Assert.Equal("unchanged", File.ReadAllText(
            Path.Combine(parked, "node_modules", "package.js")
        ));
        Assert.Equal(WorkspaceSessionState.Active,
            (await fixture.SessionAsync(newOwner.OperationId))!.State);
        Assert.Empty(await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
            .AuditAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_UserFacingHandlerRejectsInvalidRepoAndStaleSelectionWithoutCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        int openedEditors = fixture.Editor.OpenedPaths.Count;

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.OpenInReadySlotWithHandlerAsync(
                "http://github.com/owner/repo/pull/18",
                slot.SlotId,
                slot.Generation
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenInReadySlotWithHandlerAsync(
                "https://github.com/another/repo/pull/18",
                slot.SlotId,
                slot.Generation
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenInReadySlotWithHandlerAsync(
                "https://github.com/owner/repo/pull/17",
                slot.SlotId,
                slot.Generation
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenInReadySlotWithHandlerAsync(
                "https://github.com/owner/repo/pull/18",
                slot.SlotId,
                slot.Generation + 1
            )
        );

        Assert.Equal(openedEditors, fixture.Editor.OpenedPaths.Count);
        Assert.Equal(WorkspaceSlotState.Ready,
            (await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.False(Directory.Exists(slot.WorkspacePath));
        Assert.Contains(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .ListReadyAsync(CancellationToken.None),
            candidate => candidate.SlotId == slot.SlotId
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_RejectsStaleAndOccupiedExplicitAllocation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession first = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(first.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(first.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);

        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenInReadySlotAsync(
                "https://github.com/owner/repo/pull/18",
                slot.SlotId,
                slot.Generation + 1
            )
        );

        Directory.CreateDirectory(slot.WorkspacePath);
        string foreign = Path.Combine(slot.WorkspacePath, "foreign.txt");
        File.WriteAllText(foreign, "do not delete");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenInReadySlotAsync(
                "https://github.com/owner/repo/pull/18",
                slot.SlotId,
                slot.Generation
            )
        );
        Assert.Equal("do not delete", File.ReadAllText(foreign));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal(WorkspaceSlotState.Ready, (await new SqliteWorkspaceSlotRegistry(
            fixture.DatabasePath
        ).FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
        Assert.Empty(await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
            .ListActiveAsync(CancellationToken.None));
        Directory.Delete(slot.WorkspacePath, recursive: true);
        Assert.False(Directory.Exists(slot.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_ReservationCannotBeIssuedTwiceAcrossConnections()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession first = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(first.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(first.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);

        SqliteWorkspaceSlotRegistry one = new(fixture.DatabasePath);
        SqliteWorkspaceSlotRegistry two = new(fixture.DatabasePath);
        Task<WorkspaceReadySlotReservation?> a = one.TryReserveAsync(
            prepared.SlotId, prepared.Generation, "github:owner/repo", 18,
            fixture.SourcePath, CancellationToken.None
        );
        Task<WorkspaceReadySlotReservation?> b = two.TryReserveAsync(
            prepared.SlotId, prepared.Generation, "github:owner/repo", 19,
            fixture.SourcePath, CancellationToken.None
        );
        WorkspaceReadySlotReservation?[] results = await Task.WhenAll(a, b);
        WorkspaceReadySlotReservation winner = Assert.Single(
            results.Where(item => item is not null)
        )!;
        Assert.Equal(1, results.Count(item => item is null));
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Reserved, slot.State);
        Assert.Equal(prepared.Generation + 1, slot.Generation);
        Assert.Equal(WorkspaceSessionState.Preparing,
            (await fixture.SessionAsync(winner.Session.OperationId))!.State);
        Assert.False(Directory.Exists(slot.WorkspacePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Null(await one.TryReserveAsync(prepared.SlotId,
            prepared.Generation, "github:owner/repo", 20,
            fixture.SourcePath, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task Release_RealProcessKillAtBothDurableBoundariesNeverLosesParkedUserWork()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "must survive release crash");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "staged original");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "unstaged original");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);

        string signalled = await SubprocessCrashTests.KillAtSlotHandoffCheckpointAsync(
            "prepare-release", fixture.DatabasePath, owner.OperationId,
            fixture.SignalRoot, WorkspaceSlotHandoffCheckpoint.ReleasePrepared,
            fixture.SignalRoot
        );
        Assert.Equal(opened.WorkspacePath, signalled);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceSlotReleasePreparation pending = Assert.Single(
            await registry.ListPreparedAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotReleasePreparationState.Prepared, pending.State);
        Assert.Equal(WorkspaceSlotState.Protected,
            (await registry.FindSlotAsync(pending.SlotId, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSessionState.Parked,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        Assert.Equal("must survive release crash", File.ReadAllText(
            Path.Combine(parked, "notes.local")
        ));
        Assert.True(await registry.CancelAsync(pending.PreparationId, CancellationToken.None));

        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(
            owner.OperationId
        );
        signalled = await SubprocessCrashTests.KillAtSlotHandoffCheckpointAsync(
            "release", fixture.DatabasePath, prepared.PreparationId,
            fixture.SignalRoot, WorkspaceSlotHandoffCheckpoint.ReleaseCommitted,
            fixture.SignalRoot
        );
        Assert.Equal(opened.WorkspacePath, signalled);
        Assert.Equal(WorkspaceSlotReleasePreparationState.Committed,
            (await registry.FindPreparationAsync(prepared.PreparationId, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSlotState.Ready,
            (await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSessionState.Released,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        Assert.Null(await registry.FindHeldSlotAsync(owner.OperationId, CancellationToken.None));
        Assert.False(Directory.Exists(opened.WorkspacePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal("must survive release crash", File.ReadAllText(
            Path.Combine(parked, "notes.local")
        ));
        Assert.Equal("unstaged original", File.ReadAllText(Path.Combine(parked, "feature.txt")));
        Assert.Equal("staged original", await fixture.ReadStagedFeatureAsync(parked));
        Assert.NotNull(await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
            .GetSnapshotAsync(snapshot.SnapshotId, CancellationToken.None));

        // A crash after atomic commit is idempotent and cannot reclaim old ownership.
        Assert.Equal(WorkspaceSlotReleasePreparationState.Committed,
            (await fixture.FinalizeReleaseAsync(prepared.PreparationId)).State);
        Assert.Empty(await registry.AuditAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task ReadySlot_RealProcessKillAtEachBoundaryPreservesBothOwners()
    {
        WorkspaceSlotHandoffCheckpoint[] points =
        [
            WorkspaceSlotHandoffCheckpoint.SlotReserved,
            WorkspaceSlotHandoffCheckpoint.CommitPinned,
            WorkspaceSlotHandoffCheckpoint.WorktreeCreated,
            WorkspaceSlotHandoffCheckpoint.SlotActivated,
        ];

        foreach (WorkspaceSlotHandoffCheckpoint point in points)
        {
            using Fixture fixture = await Fixture.CreateAsync();
            OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
            WorkspaceSession original = await fixture.OwnedSessionAsync();
            File.WriteAllText(
                Path.Combine(opened.WorkspacePath, "notes.local"), "precious old PR data"
            );
            string parked = await fixture.ParkAsync(original.OperationId);
            WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(
                original.OperationId
            );
            _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
            await fixture.CreatePullRequestAsync(18);

            string signalled = await SubprocessCrashTests.KillAtSlotHandoffCheckpointAsync(
                "acquire", fixture.DatabasePath, prepared.SlotId,
                "https://github.com/owner/repo/pull/18", point, fixture.SignalRoot
            );
            Assert.Equal(opened.WorkspacePath, signalled);

            SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
            WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
                await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None)
            );
            Assert.Equal(prepared.Generation + 1, slot.Generation);
            Assert.Equal(WorkspaceSessionState.Released,
                (await fixture.SessionAsync(original.OperationId))!.State);
            Assert.Equal("precious old PR data", File.ReadAllText(
                Path.Combine(parked, "notes.local")
            ));
            Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
            Assert.Null(await registry.FindHeldSlotAsync(original.OperationId, CancellationToken.None));

            if (point == WorkspaceSlotHandoffCheckpoint.SlotActivated)
            {
                Assert.Equal(WorkspaceSlotState.Protected, slot.State);
                Assert.Empty(await registry.ListReservedAsync(CancellationToken.None));
                OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(
                    "https://github.com/owner/repo/pull/18"
                );
                Assert.True(reopened.Reused);
                Assert.Equal(slot.WorkspacePath, reopened.WorkspacePath);
                Assert.Empty(await registry.AuditAsync(CancellationToken.None));
                continue;
            }

            Assert.Equal(WorkspaceSlotState.Reserved, slot.State);
            WorkspaceReadySlotReservation reservation = Assert.IsType<WorkspaceReadySlotReservation>(
                await registry.FindReservedAsync(slot.SlotId, CancellationToken.None)
            );
            Assert.Equal(WorkspaceSessionState.Preparing,
                (await fixture.SessionAsync(reservation.Session.OperationId))!.State);
            Assert.Contains(
                await fixture.ScanRecoveryAsync(),
                f => f.Kind == WorkspaceRecoveryKind.PendingSlotCheckout
                    && f.Path == slot.WorkspacePath
            );
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.OpenWithFreshHandlerAsync(
                    "https://github.com/owner/repo/pull/18"
                )
            );

            if (point == WorkspaceSlotHandoffCheckpoint.WorktreeCreated)
            {
                Assert.True(await fixture.IsRegisteredWorktreeAsync(slot.WorkspacePath));
                WorkspaceSession recovered = await fixture.ReconcileReadySlotAsync(slot.SlotId);
                Assert.Equal(WorkspaceSessionState.Active, recovered.State);
                Assert.Equal(WorkspaceSlotState.Protected,
                    (await registry.FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
                Assert.Empty(await registry.ListReservedAsync(CancellationToken.None));
            }
            else
            {
                Assert.False(Directory.Exists(slot.WorkspacePath));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => fixture.ReconcileReadySlotAsync(slot.SlotId)
                );
                Assert.Equal(WorkspaceSlotState.Reserved,
                    (await registry.FindSlotAsync(slot.SlotId, CancellationToken.None))!.State);
                Assert.Null(await registry.TryReserveAsync(
                    slot.SlotId, prepared.Generation, "github:owner/repo",
                    19, fixture.SourcePath, CancellationToken.None
                ));
            }

            Assert.Equal("precious old PR data", File.ReadAllText(
                Path.Combine(parked, "notes.local")
            ));
        }
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_InterruptedVerifiedCheckoutCanActivateAfterRestart()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult first = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(first.WorkspacePath, "private.local"), "important old data");
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceReadySlotReservation reservation = Assert.IsType<WorkspaceReadySlotReservation>(
            await registry.TryReserveAsync(
                prepared.SlotId, prepared.Generation, "github:owner/repo",
                18, fixture.SourcePath, CancellationToken.None
            )
        );
        GitCliWorkspaceGit git = new();
        await git.FetchPullRequestAsync(fixture.SourcePath, 18, CancellationToken.None);
        string commit = await git.GetPullRequestCommitAsync(
            fixture.SourcePath, 18, CancellationToken.None
        );
        await new SqliteWorkspaceSessionJournal(fixture.DatabasePath).PinCommitAsync(
            reservation.Session.OperationId, commit, CancellationToken.None
        );
        await git.CreateWorktreeAsync(
            fixture.SourcePath, reservation.Session.WorkspacePath, commit, CancellationToken.None
        );

        // Simulate app restart after Git checkout but before the Active/Protected CAS.
        WorkspaceSlot pending = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Reserved, pending.State);
        Assert.Equal(WorkspaceSessionState.Preparing,
            (await fixture.SessionAsync(reservation.Session.OperationId))!.State);
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotCheckout
                && finding.Path == reservation.Session.WorkspacePath
                && finding.Description.Contains("PR #18")
                && finding.Description.Contains(prepared.SlotId.ToString("N"))
        );
        // The ordinary PR opener must never silently activate a Reserved slot.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync("https://github.com/owner/repo/pull/18")
        );
        Assert.Equal(WorkspaceSlotState.Reserved,
            (await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);

        // The tray binds explicit consent to the selected lease and pinned
        // commit. A stale or substituted selection cannot activate anything.
        WorkspaceReadySlotReservation selected = Assert.Single(
            await registry.ListReservedAsync(CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ReconcileSelectedReadySlotAsync(
                selected with { Lease = selected.Lease with { LeaseId = Guid.NewGuid() } }
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ReconcileSelectedReadySlotAsync(
                selected with
                {
                    Session = selected.Session with { PinnedCommitSha = new string('f', 40) },
                }
            )
        );
        Assert.Equal(WorkspaceSlotState.Reserved,
            (await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);

        WorkspaceSession recovered = await fixture.ReconcileSelectedReadySlotAsync(selected);
        Assert.Equal(reservation.Session.OperationId, recovered.OperationId);
        Assert.Equal(WorkspaceSessionState.Active, recovered.State);
        Assert.Equal(WorkspaceSlotState.Protected,
            (await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);
        Assert.Equal("feature for pr 18", File.ReadAllText(
            Path.Combine(recovered.WorkspacePath, "feature.txt")
        ));
        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(
            "https://github.com/owner/repo/pull/18"
        );
        Assert.True(reopened.Reused);
        Assert.Equal(recovered.WorkspacePath, reopened.WorkspacePath);
        Assert.Equal("important old data", File.ReadAllText(
            Path.Combine(parked, "private.local")
        ));
        Assert.Empty(await registry.AuditAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotCheckout
                && finding.Path == recovered.WorkspacePath
        );
        Assert.Empty(await registry.ListReservedAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_InterruptedDirtyCheckoutRemainsReservedWithUserData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);
        await fixture.CreatePullRequestAsync(18);

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceReadySlotReservation reservation = Assert.IsType<WorkspaceReadySlotReservation>(
            await registry.TryReserveAsync(
                prepared.SlotId, prepared.Generation, "github:owner/repo",
                18, fixture.SourcePath, CancellationToken.None
            )
        );
        GitCliWorkspaceGit git = new();
        await git.FetchPullRequestAsync(fixture.SourcePath, 18, CancellationToken.None);
        string commit = await git.GetPullRequestCommitAsync(
            fixture.SourcePath, 18, CancellationToken.None
        );
        await new SqliteWorkspaceSessionJournal(fixture.DatabasePath).PinCommitAsync(
            reservation.Session.OperationId, commit, CancellationToken.None
        );
        await git.CreateWorktreeAsync(
            fixture.SourcePath, reservation.Session.WorkspacePath, commit, CancellationToken.None
        );
        string privateFile = Path.Combine(reservation.Session.WorkspacePath, "important.local");
        File.WriteAllText(privateFile, "do not erase");

        WorkspaceReadySlotReservation selected = Assert.Single(
            await registry.ListReservedAsync(CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReconcileSelectedReadySlotAsync(selected)
        );
        Assert.Equal(WorkspaceSlotState.Reserved,
            (await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSessionState.Preparing,
            (await fixture.SessionAsync(reservation.Session.OperationId))!.State);
        Assert.Equal("do not erase", File.ReadAllText(privateFile));
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotCheckout
                && finding.Path == reservation.Session.WorkspacePath
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync("https://github.com/owner/repo/pull/18")
        );
        Assert.Equal("do not erase", File.ReadAllText(privateFile));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.False(File.Exists(Path.Combine(parked, "important.local")));
        Assert.Null(await registry.TryReserveAsync(
            prepared.SlotId, prepared.Generation, "github:owner/repo",
            19, fixture.SourcePath, CancellationToken.None
        ));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReadySlot_InterruptedMissingCheckoutCannotActivate()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        _ = await fixture.FinalizeReleaseAsync(prepared.PreparationId);

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceReadySlotReservation reservation = Assert.IsType<WorkspaceReadySlotReservation>(
            await registry.TryReserveAsync(
                prepared.SlotId, prepared.Generation, "github:owner/repo",
                18, fixture.SourcePath, CancellationToken.None
            )
        );
        WorkspaceReadySlotReservation selected = Assert.Single(
            await registry.ListReservedAsync(CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReconcileSelectedReadySlotAsync(selected)
        );
        // Even direct legacy activation cannot bypass the Protected-slot CAS.
        await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
            .PinCommitAsync(reservation.Session.OperationId,
                new string('a', 40), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ActivateAsync(reservation.Session.OperationId, CancellationToken.None)
        );
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.PendingSlotCheckout
                && finding.Path == reservation.Session.WorkspacePath
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenWithFreshHandlerAsync("https://github.com/owner/repo/pull/18")
        );
        Assert.Equal(WorkspaceSlotState.Reserved,
            (await registry.FindSlotAsync(prepared.SlotId, CancellationToken.None))!.State);
        Assert.Equal(WorkspaceSessionState.Preparing,
            (await fixture.SessionAsync(reservation.Session.OperationId))!.State);
        Assert.False(Directory.Exists(reservation.Session.WorkspacePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleaseCommit_RejectsOccupiedOriginalAndAllowsCancel()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        Directory.CreateDirectory(owner.WorkspacePath);
        string foreign = Path.Combine(owner.WorkspacePath, "foreign.txt");
        File.WriteAllText(foreign, "never overwrite");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.FinalizeReleaseAsync(prepared.PreparationId)
        );

        Assert.Equal("never overwrite", File.ReadAllText(foreign));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal(WorkspaceSessionState.Parked,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindHeldSlotAsync(owner.OperationId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, slot.State);
        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        Assert.Equal(WorkspaceSlotReleasePreparationState.Prepared,
            (await registry.FindPreparationAsync(prepared.PreparationId, CancellationToken.None))!.State);
        Assert.True(await registry.CancelAsync(prepared.PreparationId, CancellationToken.None));
        Directory.Delete(owner.WorkspacePath, recursive: true);
        Assert.Equal(owner.WorkspacePath, await fixture.UnparkAsync(owner.OperationId));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleaseCommit_RejectsStaleGenerationWithoutReleasingLease()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        WorkspaceSlotReleasePreparation prepared = await fixture.PrepareReleaseAsync(owner.OperationId);
        using (Microsoft.Data.Sqlite.SqliteConnection connection = new(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        ))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE workspace_slots SET generation = generation + 1 WHERE slot_id = $id";
            update.Parameters.AddWithValue("$id", prepared.SlotId.ToString("N"));
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.FinalizeReleaseAsync(prepared.PreparationId)
        );

        WorkspaceSlot slot = Assert.IsType<WorkspaceSlot>(
            await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindSlotAsync(prepared.SlotId, CancellationToken.None)
        );
        Assert.Equal(WorkspaceSlotState.Protected, slot.State);
        Assert.Equal(prepared.Generation + 1, slot.Generation);
        Assert.Equal(WorkspaceSessionState.Parked,
            (await fixture.SessionAsync(owner.OperationId))!.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
        Assert.Equal(WorkspaceSlotReleasePreparationState.Prepared,
            (await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
                .FindPreparationAsync(prepared.PreparationId, CancellationToken.None))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasePreparation_RejectsOccupiedOriginalWithoutWritingIntent()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);
        Directory.CreateDirectory(owner.WorkspacePath);
        string unknown = Path.Combine(owner.WorkspacePath, "not-owned.txt");
        File.WriteAllText(unknown, "foreign");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.PrepareReleaseAsync(owner.OperationId)
        );

        Assert.Empty(await new SqliteWorkspaceSlotRegistry(fixture.DatabasePath)
            .ListPreparedAsync(CancellationToken.None));
        Assert.Equal("foreign", File.ReadAllText(unknown));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasePreflight_FailsClosedOnOccupiedOriginalOrMissingParkedPath()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        string parked = await fixture.ParkAsync(owner.OperationId);

        Directory.CreateDirectory(owner.WorkspacePath);
        File.WriteAllText(Path.Combine(owner.WorkspacePath, "foreign.txt"), "not pool-owned");
        Assert.Equal(
            WorkspaceReleaseBlocker.OriginalPathOccupied,
            (await fixture.AssessReleaseAsync(owner.OperationId)).Blocker
        );
        Assert.Equal("not pool-owned", File.ReadAllText(
            Path.Combine(owner.WorkspacePath, "foreign.txt")
        ));
        Directory.Delete(owner.WorkspacePath, recursive: true);

        string retained = Path.Combine(parked, "retained.txt");
        File.WriteAllText(retained, "keep");
        string temporarilyMoved = parked + "-inspection";
        Directory.Move(parked, temporarilyMoved);

        try
        {
            Assert.Equal(
                WorkspaceReleaseBlocker.ParkedWorktreeMissing,
                (await fixture.AssessReleaseAsync(owner.OperationId)).Blocker
            );
        }
        finally
        {
            Directory.Move(temporarilyMoved, parked);
        }

        Assert.Equal("keep", File.ReadAllText(retained));
        Assert.True((await fixture.AssessReleaseAsync(owner.OperationId)).CanBeginRelease);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task ReleasePreflight_RefusesUnprotectedOrUnknownLeaseState()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession owner = await fixture.OwnedSessionAsync();
        _ = await fixture.ParkAsync(owner.OperationId);

        Assert.Equal(
            WorkspaceReleaseBlocker.UnknownSession,
            (await fixture.AssessReleaseAsync(Guid.NewGuid())).Blocker
        );

        SqliteWorkspaceSlotRegistry registry = new(fixture.DatabasePath);
        WorkspaceSlot protectedSlot = Assert.IsType<WorkspaceSlot>(
            await registry.FindHeldSlotAsync(owner.OperationId, CancellationToken.None)
        );

        // Simulates a corrupted/inappropriately marked Ready slot, not a production release.
        using Microsoft.Data.Sqlite.SqliteConnection connection = new(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        );
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE workspace_slots SET state = 'Ready' WHERE slot_id = $id";
        command.Parameters.AddWithValue("$id", protectedSlot.SlotId.ToString("N"));
        Assert.Equal(1, command.ExecuteNonQuery());

        Assert.Equal(
            WorkspaceReleaseBlocker.SlotNotProtected,
            (await fixture.AssessReleaseAsync(owner.OperationId)).Blocker
        );
        Assert.Equal(WorkspaceSessionState.Parked, (await fixture.SessionAsync(owner.OperationId))!.State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task SlotAudit_RecoveryDiagnosticsFlagsConflictingLeaseWithoutMovingUserData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string userFile = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(userFile, "preserve");

        using Microsoft.Data.Sqlite.SqliteConnection db = new(
            $"Data Source={fixture.DatabasePath};Pooling=False"
        );
        db.Open();
        using Microsoft.Data.Sqlite.SqliteCommand update = db.CreateCommand();
        update.CommandText = """
            UPDATE workspace_slot_leases
               SET generation = generation + 1
             WHERE state = 'Held'
            """;
        Assert.Equal(1, update.ExecuteNonQuery());

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.SlotOwnershipInconsistent
                && finding.Description.Contains("LeaseGenerationMismatch")
        );

        Assert.Equal(
            WorkspaceReleaseBlocker.MissingLease,
            (await fixture.AssessReleaseAsync((await fixture.OwnedSessionAsync()).OperationId)).Blocker
        );
        Assert.Equal("preserve", File.ReadAllText(userFile));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Parking_UnparkRefusesOccupiedDestinationWithoutChangingEitherTree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        WorkspaceSession session = await fixture.OwnedSessionAsync();
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "notes.local"), "parked data");
        string parked = await fixture.ParkAsync(session.OperationId);

        Directory.CreateDirectory(opened.WorkspacePath);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "other-user.txt"), "foreign data");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.UnparkAsync(session.OperationId)
        );

        Assert.Equal("foreign data", File.ReadAllText(
            Path.Combine(opened.WorkspacePath, "other-user.txt")));
        Assert.Equal("parked data", File.ReadAllText(Path.Combine(parked, "notes.local")));
        Assert.Equal(WorkspaceSessionState.Parked, (await fixture.SessionAsync(session.OperationId))!.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(parked));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_RefusesForeignDirectoryWithoutDeletingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string path = fixture.WorkspacePath(17);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "user.txt"), "keep");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "user.txt")));
        Assert.Empty(fixture.Editor.OpenedPaths);
        Assert.False(await fixture.IsRegisteredWorktreeAsync(path));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_QuarantinesUnjournalledGitWorktreeWithoutDataLoss()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.CreateUnjournalledWorktreeAsync();
        string workspacePath = fixture.WorkspacePath(17);
        string notePath = Path.Combine(workspacePath, "notes.local");
        File.WriteAllText(notePath, "old preview session");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Equal("old preview session", File.ReadAllText(notePath));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(workspacePath));
        Assert.Empty(fixture.Editor.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_ConcurrentRequestsAllocateOneWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult[] results = await Task.WhenAll(
            fixture.OpenAsync(PullRequestUrl),
            fixture.OpenAsync(PullRequestUrl)
        );

        Assert.Single(results.Where(result => !result.Reused));
        Assert.Single(results.Where(result => result.Reused));
        Assert.Equal(results[0].WorkspacePath, results[1].WorkspacePath);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(results[0].WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_RecoversInterruptedCheckoutAfterRestart()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SimulateInterruptedPreparationAsync(17);
        string workspacePath = fixture.WorkspacePath(17);
        File.WriteAllText(Path.Combine(workspacePath, "notes.local"), "saved user notes");

        OpenPullRequestResult result = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);

        Assert.True(result.Reused);
        Assert.Equal("saved user notes", File.ReadAllText(Path.Combine(workspacePath, "notes.local")));
        Assert.Equal(workspacePath, Assert.Single(fixture.Editor.OpenedPaths));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_DifferentHandlersCannotDoubleAllocate()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        OpenPullRequestResult[] results = await Task.WhenAll(
            fixture.OpenAsync(PullRequestUrl),
            fixture.OpenWithFreshHandlerAsync(PullRequestUrl)
        );

        Assert.Single(results.Where(result => !result.Reused));
        Assert.Single(results.Where(result => result.Reused));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(results[0].WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task RepositoryOrigin_RealGitReaderRejectsMismatchedLocalRemote()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        PullRequestReference request = new("github", "owner", "repo", 17);
        RepositoryRegistration registration = new(
            request.RepositoryKey,
            fixture.SourcePath,
            fixture.WorkspacePath(17)
        );

        GitHubOriginVerifier verifier = new(new GitCliWorkspaceGit());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyAsync(request, registration, CancellationToken.None)
        );
        Assert.False(Directory.Exists(fixture.WorkspacePath(17)));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_PreservesIndexStagedUnstagedBinaryIgnoredAndDeletions()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string worktree = opened.WorkspacePath;

        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "staged contents");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "unstaged contents");
        File.Delete(Path.Combine(worktree, "README.md"));
        File.WriteAllBytes(Path.Combine(worktree, "untracked.bin"), [0, 255, 19, 128]);
        File.WriteAllText(Path.Combine(worktree, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(worktree, "generated"));
        File.WriteAllBytes(Path.Combine(worktree, "generated", "ignored.bin"), [255, 0, 44]);

        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string manifestPath = Path.Combine(snapshot.Folder, "manifest.json");
        Assert.True(File.Exists(manifestPath));

        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;

        Assert.Equal(2, manifest.Version);
        Assert.Contains(manifest.StagedFiles, file => file.RelativePath == "feature.txt");
        Assert.Equal(
            "staged contents",
            File.ReadAllText(Path.Combine(snapshot.Folder, "staged", "feature.txt"))
        );
        Assert.Contains("README.md", manifest.MissingPaths);
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(snapshot.Folder, "files", "feature.txt")));
        Assert.Equal(
            new byte[] { 0, 255, 19, 128 },
            File.ReadAllBytes(Path.Combine(snapshot.Folder, "files", "untracked.bin"))
        );
        Assert.Equal(
            new byte[] { 255, 0, 44 },
            File.ReadAllBytes(Path.Combine(snapshot.Folder, "files", "generated", "ignored.bin"))
        );
        Assert.True(File.Exists(Path.Combine(snapshot.Folder, "index.bin")));
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(worktree, "feature.txt")));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(worktree));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);

        // Capturing the index must preserve the exact independently staged payload.
        string stagedBlob = await fixture.ReadStagedFeatureAsync();
        Assert.Equal("staged contents", stagedBlob);

        OpenPullRequestResult reopened = await fixture.OpenWithFreshHandlerAsync(PullRequestUrl);
        Assert.True(reopened.Reused);
        Assert.Equal("unstaged contents", File.ReadAllText(Path.Combine(worktree, "feature.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RestoreIntoNewWorktreePreservesIndexAndWorkingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = opened.WorkspacePath;
        File.WriteAllText(Path.Combine(source, "feature.txt"), "staged version");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(Path.Combine(source, "feature.txt"), "working version");
        File.Delete(Path.Combine(source, "README.md"));
        File.WriteAllText(Path.Combine(source, ".gitignore"), "build/\n");
        Directory.CreateDirectory(Path.Combine(source, "build"));
        byte[] binary = [0, 255, 13, 128, 1];
        File.WriteAllBytes(Path.Combine(source, "build", "artifact.bin"), binary);
        File.WriteAllBytes(Path.Combine(source, "personal.bin"), binary);

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();

        // The source can continue evolving; restoration must use immutable saved data.
        File.WriteAllText(Path.Combine(source, "feature.txt"), "later unsaved work");

        string recovered = await fixture.RestoreSnapshotAsync(saved.SnapshotId);

        Assert.NotEqual(source, recovered);
        Assert.Equal("working version", File.ReadAllText(Path.Combine(recovered, "feature.txt")));
        Assert.Equal("staged version", await fixture.ReadStagedFeatureAsync(recovered));
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(recovered, "build", "artifact.bin")));
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(recovered, "personal.bin")));
        Assert.False(File.Exists(Path.Combine(recovered, "README.md")));
        Assert.True(await fixture.IsRegisteredWorktreeAsync(recovered));
        Assert.Equal("later unsaved work", File.ReadAllText(Path.Combine(source, "feature.txt")));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task RestoreJournal_DistinguishesInterruptedAndVerifiedRecoveries()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "retain me");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(
                snapshot.SnapshotId,
                new ThrowAtCheckpoint(SnapshotCheckpoint.RestoreFilesApplied)
            )
        );

        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceRestoreOperation interrupted = Assert.Single(
            await journal.ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, interrupted.State);

        string completePath = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);
        WorkspaceRestoreOperation[] attempts = (
            await journal.ListRecoveryRestoresAsync(CancellationToken.None)
        ).ToArray();

        Assert.Equal(2, attempts.Length);
        Assert.Single(attempts.Where(operation =>
            operation.State == WorkspaceRestoreState.Verified
            && operation.Path == completePath
        ));

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(findings, finding =>
            finding.Kind == WorkspaceRecoveryKind.InterruptedRestore
            && finding.Path == interrupted.Path
        );
        Assert.Contains(findings, finding =>
            finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
            && finding.Path == completePath
        );
        Assert.Equal("retain me", File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_PreservesIgnoredEmptyDirectoryMetadata()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = opened.WorkspacePath;
        File.WriteAllText(Path.Combine(source, ".gitignore"), "generated/\n");
        string ignoredFolder = Path.Combine(source, "generated");
        string emptyFolder = Path.Combine(ignoredFolder, "empty");
        Directory.CreateDirectory(emptyFolder);
        DateTime original = new(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(emptyFolder, original);

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        string restoredEmpty = Path.Combine(restored, "generated", "empty");

        Assert.True(Directory.Exists(restoredEmpty));
        Assert.InRange(
            Math.Abs((Directory.GetLastWriteTimeUtc(restoredEmpty) - original).TotalSeconds),
            0,
            2
        );
        Assert.True(Directory.Exists(emptyFolder));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_TamperedPayloadIsRejectedBeforeNewWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "original");

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        File.WriteAllText(Path.Combine(saved.Folder, "files", "feature.txt"), "tampered!");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(saved.SnapshotId)
        );

        Assert.Equal("original", File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt")));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_PathTraversalManifestIsRejectedBeforeCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "preserve this");

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        string manifestPath = Path.Combine(saved.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        int firstFile = manifest.Files.FindIndex(file => !file.IsDirectory);
        Assert.True(firstFile >= 0);
        manifest.Files[firstFile] = manifest.Files[firstFile] with
        {
            RelativePath = "../outside.txt",
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(saved.SnapshotId)
        );

        Assert.Equal("preserve this", File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt")));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData("feature.txt.")]
    [InlineData("feature.txt ")]
    [InlineData("CON.txt")]
    [InlineData("NUL")]
    [InlineData("feature//second.txt")]
    [InlineData("dir\\\\..\\\\feature.txt")]
    public async Task Snapshot_UnsafeWindowsAliasInManifestIsRejectedBeforeAllocation(string unsafePath)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "preserve user edits");

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string manifestPath = Path.Combine(receipt.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        int fileIndex = manifest.Files.FindIndex(file => !file.IsDirectory);
        Assert.True(fileIndex >= 0);
        manifest.Files[fileIndex] = manifest.Files[fileIndex] with { RelativePath = unsafePath };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(receipt.SnapshotId)
        );

        Assert.Contains("path", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve user edits", File.ReadAllText(original));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
        Assert.Empty(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_CaseAliasedPathsAreRejectedBeforeAllocation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "preserve case-sensitive user data");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        string manifestPath = Path.Combine(receipt.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        WorkspaceSnapshotFile file = Assert.Single(
            manifest.Files.Where(item => !item.IsDirectory)
        );
        manifest.Files.Add(file with { RelativePath = file.RelativePath.ToUpperInvariant() });
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(receipt.SnapshotId)
        );

        Assert.Contains("Duplicate snapshot working path", failure.Message);
        Assert.Equal("preserve case-sensitive user data", File.ReadAllText(original));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_ReparsePointMetadataIsRejectedBeforeAllocation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "original stays");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        string manifestPath = Path.Combine(receipt.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        int index = manifest.Files.FindIndex(item => !item.IsDirectory);
        Assert.True(index >= 0);
        manifest.Files[index] = manifest.Files[index] with
        {
            Attributes = manifest.Files[index].Attributes | FileAttributes.ReparsePoint,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(receipt.SnapshotId)
        );

        Assert.Contains("reparse point", failure.Message);
        Assert.Equal("original stays", File.ReadAllText(original));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_FileAncestorCollisionIsRejectedBeforeAllocation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string sourceFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(sourceFile, "keep me");

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string manifestPath = Path.Combine(receipt.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        manifest.MissingPaths.Add("feature.txt/nested.txt");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(receipt.SnapshotId)
        );

        Assert.Contains("ancestor collision", failure.Message);
        Assert.Equal("keep me", File.ReadAllText(sourceFile));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
        Assert.Empty(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
    }

    [Theory]
    [Trait("Category", "FastE2E")]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Snapshot_MissingOrUnsupportedManifestVersionIsRejectedBeforeAllocation(int? version)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string localFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(localFile, "keep the original");

        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        string path = Path.Combine(saved.Folder, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        if (version.HasValue)
        {
            manifest["Version"] = version.Value;
        }
        else
        {
            manifest.Remove("Version");
        }

        File.WriteAllText(path, manifest.ToJsonString());

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(saved.SnapshotId)
        );

        Assert.Contains("version", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep the original", File.ReadAllText(localFile));
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
        Assert.Empty(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_UnmergedIndexFailsBeforeCreatingSnapshotStorage()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string feature = Path.Combine(opened.WorkspacePath, "feature.txt");

        await fixture.GitInWorkspaceAsync("config", "user.name", "E2E Fixture");
        await fixture.GitInWorkspaceAsync("config", "user.email", "test@example.invalid");
        await fixture.GitInWorkspaceAsync("checkout", "-b", "snapshot-ours");
        File.WriteAllText(feature, "ours\n");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        await fixture.GitInWorkspaceAsync("commit", "-m", "ours");

        await fixture.GitInWorkspaceAsync(
            "checkout", "-b", "snapshot-theirs", "HEAD~1"
        );
        File.WriteAllText(feature, "theirs\n");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        await fixture.GitInWorkspaceAsync("commit", "-m", "theirs");

        // A merge conflict creates stages 1/2/3 in the real Git index.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.GitInWorkspaceAsync("merge", "--no-commit", "snapshot-ours")
        );
        string conflictContents = File.ReadAllText(feature);
        Assert.Contains("ours", conflictContents);
        Assert.Contains("theirs", conflictContents);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync()
        );

        Assert.Contains("Unmerged", failure.Message);
        Assert.Equal(conflictContents, File.ReadAllText(feature));
        Assert.False(Directory.Exists(fixture.SnapshotRoot));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_SubmoduleIndexFailsBeforeCreatingSnapshotStorage()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string head = await new GitCliWorkspaceGit().GetWorktreeCommitAsync(
            opened.WorkspacePath, CancellationToken.None
        );

        await fixture.GitInWorkspaceAsync(
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{head},external/submodule"
        );
        string note = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(note, "never remove");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync()
        );

        Assert.Contains("submodule", failure.Message);
        Assert.Equal("never remove", File.ReadAllText(note));
        Assert.False(Directory.Exists(fixture.SnapshotRoot));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.True(await fixture.IsRegisteredWorktreeAsync(opened.WorkspacePath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_UnknownSnapshotCannotAllocateRecoveryWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(Guid.NewGuid())
        );
        Assert.False(Directory.Exists(fixture.RecoveryRoot));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_FaultsLeaveSourceAndOwnershipUnchanged()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string path = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(path, "retain user changes");

        SnapshotCheckpoint[] checkpoints =
        [
            SnapshotCheckpoint.ExportIndexCopied,
            SnapshotCheckpoint.ExportReadyToPublish,
            SnapshotCheckpoint.ExportPublished,
        ];

        foreach (SnapshotCheckpoint checkpoint in checkpoints)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.SnapshotAsync(new ThrowAtCheckpoint(checkpoint))
            );
            Assert.Contains(checkpoint.ToString(), error.Message);
            Assert.Equal("retain user changes", File.ReadAllText(path));
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
            Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        }

        Assert.Single(
            Directory.GetDirectories(
                fixture.SnapshotRoot,
                "snapshot-*",
                SearchOption.AllDirectories
            )
        );

        WorkspaceSnapshotReceipt retry = await fixture.SnapshotAsync();
        Assert.True(File.Exists(Path.Combine(retry.Folder, "manifest.json")));
        Assert.Single(await fixture.ListPublishedSnapshotsAsync());
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RestoreFaultsRetainIsolatedOrphansAndOriginalFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "recoverable contents");

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        SnapshotCheckpoint[] checkpoints =
        [
            SnapshotCheckpoint.RestoreWorktreeCreated,
            SnapshotCheckpoint.RestoreFilesApplied,
            SnapshotCheckpoint.RestoreIndexApplied,
        ];

        for (int i = 0; i < checkpoints.Length; i++)
        {
            SnapshotCheckpoint checkpoint = checkpoints[i];
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.RestoreSnapshotAsync(
                    receipt.SnapshotId,
                    new ThrowAtCheckpoint(checkpoint)
                )
            );

            Assert.Contains(checkpoint.ToString(), error.Message);
            Assert.Equal("recoverable contents", File.ReadAllText(original));
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
            string[] recovered = Directory.GetDirectories(fixture.RecoveryRoot);
            Assert.Equal(i + 1, recovered.Length);

            foreach (string orphan in recovered)
            {
                Assert.True(await fixture.IsRegisteredWorktreeAsync(orphan));
            }
        }

        string successful = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        Assert.Equal("recoverable contents", File.ReadAllText(Path.Combine(successful, "feature.txt")));
        Assert.Equal("recoverable contents", File.ReadAllText(original));
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task Snapshot_RealProcessKillPreservesSourceAndCanRetry()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "unsaved contents");
        WorkspaceSession owner = await fixture.OwnedSessionAsync();

        SnapshotCheckpoint[] boundaries =
        [
            SnapshotCheckpoint.ExportIndexCopied,
            SnapshotCheckpoint.ExportReadyToPublish,
            SnapshotCheckpoint.ExportPublished,
        ];

        foreach (SnapshotCheckpoint checkpoint in boundaries)
        {
            string pendingOrPublished = await SubprocessCrashTests.KillAtCheckpointAsync(
                "export",
                fixture.DatabasePath,
                owner.OperationId,
                fixture.SnapshotRoot,
                checkpoint,
                fixture.SignalRoot
            );

            Assert.True(Directory.Exists(pendingOrPublished));
            Assert.Equal("unsaved contents", File.ReadAllText(changed));
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
            Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        }

        // The killed process must release its OS file lock; a new process can retry.
        WorkspaceSnapshotReceipt retry = await fixture.SnapshotAsync();
        Assert.True(File.Exists(Path.Combine(retry.Folder, "manifest.json")));
        Assert.Single(await fixture.ListPublishedSnapshotsAsync());
    }

    [Fact]
    [Trait("Category", "SlowE2E")]
    public async Task Snapshot_RealProcessKillLeavesRecoverableIsolatedWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "original user work");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        SnapshotCheckpoint[] boundaries =
        [
            SnapshotCheckpoint.RestoreWorktreeCreated,
            SnapshotCheckpoint.RestoreFilesApplied,
            SnapshotCheckpoint.RestoreIndexApplied,
        ];

        foreach (SnapshotCheckpoint checkpoint in boundaries)
        {
            string orphan = await SubprocessCrashTests.KillAtCheckpointAsync(
                "restore",
                fixture.DatabasePath,
                receipt.SnapshotId,
                fixture.RecoveryRoot,
                checkpoint,
                fixture.SignalRoot
            );

            Assert.True(Directory.Exists(orphan));
            Assert.True(await fixture.IsRegisteredWorktreeAsync(orphan));
            Assert.Equal("original user work", File.ReadAllText(changed));
            Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
        }

        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        Assert.Equal("original user work", File.ReadAllText(Path.Combine(restored, "feature.txt")));
        Assert.Equal("original user work", File.ReadAllText(changed));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RejectsWorkingFileMutationImmediatelyBeforePublish()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "first user change");

        ISnapshotCheckpointSink mutator = new MutateAtCheckpoint(
            SnapshotCheckpoint.ExportReadyToPublish,
            () =>
            {
                File.WriteAllText(changed, "second user change");
                return Task.CompletedTask;
            }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync(mutator)
        );

        Assert.Equal("second user change", File.ReadAllText(changed));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.Empty(Directory.GetDirectories(fixture.SnapshotRoot, "snapshot-*", SearchOption.AllDirectories));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);

        WorkspaceSnapshotReceipt retry = await fixture.SnapshotAsync();
        Assert.Equal(
            "second user change",
            File.ReadAllText(Path.Combine(retry.Folder, "files", "feature.txt"))
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RejectsGitIndexMutationDuringCapture()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changed = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changed, "unstaged v1");

        ISnapshotCheckpointSink mutator = new MutateAtCheckpoint(
            SnapshotCheckpoint.ExportIndexCopied,
            async () =>
            {
                File.WriteAllText(changed, "staged v2");
                await fixture.GitInWorkspaceAsync("add", "feature.txt");
            }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync(mutator)
        );

        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.Equal("staged v2", await fixture.ReadStagedFeatureAsync());
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);

        WorkspaceSnapshotReceipt retry = await fixture.SnapshotAsync();
        Assert.True(File.Exists(Path.Combine(retry.Folder, "index.bin")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task RecoveryInventory_DiscoversPendingAndUnjournalledSnapshotAfterFailure()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string sourceFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(sourceFile, "user edits must survive");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync(
                new ThrowAtCheckpoint(SnapshotCheckpoint.ExportIndexCopied)
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SnapshotAsync(
                new ThrowAtCheckpoint(SnapshotCheckpoint.ExportPublished)
            )
        );

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();

        Assert.Contains(findings, finding => finding.Kind == WorkspaceRecoveryKind.IncompleteSnapshot);
        Assert.Contains(findings, finding => finding.Kind == WorkspaceRecoveryKind.UnjournalledSnapshot);
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.Equal("user edits must survive", File.ReadAllText(sourceFile));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task RecoveryInventory_FindsUnmanagedRestoreAndMissingJournalledSnapshot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string sourceFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(sourceFile, "keep me");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(
                receipt.SnapshotId,
                new ThrowAtCheckpoint(SnapshotCheckpoint.RestoreIndexApplied)
            )
        );

        IReadOnlyList<WorkspaceRecoveryFinding> orphaned = await fixture.ScanRecoveryAsync();
        WorkspaceRecoveryFinding recovery = Assert.Single(
            orphaned.Where(finding =>
                finding.Kind == WorkspaceRecoveryKind.InterruptedRestore
            )
        );
        Assert.True(await fixture.IsRegisteredWorktreeAsync(recovery.Path));

        // Simulate external removal/movement without ever deleting user data.
        Directory.Move(receipt.Folder, receipt.Folder + "-relocated");
        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding =>
                finding.Kind == WorkspaceRecoveryKind.MissingSnapshot
                && finding.SnapshotId == receipt.SnapshotId
        );
        Assert.Equal("keep me", File.ReadAllText(sourceFile));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_InsufficientDiskSpaceFailsBeforeStaging()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string userFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(userFile, "important local work");

        await Assert.ThrowsAsync<IOException>(
            () => fixture.SnapshotAsync(diskSpace: new FixedDiskSpaceProbe(0))
        );

        Assert.Equal("important local work", File.ReadAllText(userFile));
        Assert.False(Directory.Exists(fixture.SnapshotRoot));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_InsufficientDiskSpaceDoesNotAllocateOrReserve()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, "feature.txt"), "important local work");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();

        await Assert.ThrowsAsync<IOException>(
            () => fixture.RestoreSnapshotAsync(
                snapshot.SnapshotId,
                diskSpace: new FixedDiskSpaceProbe(0)
            )
        );

        Assert.False(Directory.Exists(fixture.RecoveryRoot));
        Assert.Empty(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(
            "important local work",
            File.ReadAllText(Path.Combine(opened.WorkspacePath, "feature.txt"))
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_MidCopyIoFailureRetainsIncompleteSnapshotAndUserFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "local-large.bin");
        byte[] userBytes = Enumerable.Repeat((byte)0xA5, 256 * 1024).ToArray();
        File.WriteAllBytes(original, userBytes);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => fixture.SnapshotAsync(
                copyProgress: new FailCopyAfterBytes(SnapshotCopyPhase.ExportWorkingFile, 65536)
            )
        );

        Assert.Contains("disk full", error.Message);
        Assert.Equal(userBytes, File.ReadAllBytes(original));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());

        string pending = Assert.Single(
            Directory.GetDirectories(
                fixture.SnapshotRoot,
                ".pending-*",
                SearchOption.AllDirectories
            )
        );
        string partial = Path.Combine(pending, "files", "local-large.bin");
        Assert.True(File.Exists(partial));
        Assert.InRange(new FileInfo(partial).Length, 1, userBytes.Length - 1);
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            item => item.Kind == WorkspaceRecoveryKind.IncompleteSnapshot
        );

        WorkspaceSnapshotReceipt retry = await fixture.SnapshotAsync();
        Assert.Equal(userBytes, File.ReadAllBytes(Path.Combine(retry.Folder, "files", "local-large.bin")));
        Assert.Equal(userBytes, File.ReadAllBytes(original));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_MidCopyIoFailureRetainsJournalledOrphanAndAllowsRetry()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "local-large.bin");
        byte[] userBytes = Enumerable.Repeat((byte)0x5A, 256 * 1024).ToArray();
        File.WriteAllBytes(original, userBytes);
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => fixture.RestoreSnapshotAsync(
                snapshot.SnapshotId,
                copyProgress: new FailCopyAfterBytes(SnapshotCopyPhase.RestoreWorkingFile, 65536)
            )
        );

        Assert.Contains("disk full", error.Message);
        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceRestoreOperation failed = Assert.Single(
            await journal.ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, failed.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(failed.Path));
        string partial = Path.Combine(failed.Path, "local-large.bin");
        Assert.InRange(new FileInfo(partial).Length, 1, userBytes.Length - 1);
        Assert.Equal(userBytes, File.ReadAllBytes(original));
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            item => item.Kind == WorkspaceRecoveryKind.InterruptedRestore
                && item.Path == failed.Path
        );

        string retry = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);
        Assert.NotEqual(failed.Path, retry);
        Assert.Equal(userBytes, File.ReadAllBytes(Path.Combine(retry, "local-large.bin")));
        Assert.Equal(userBytes, File.ReadAllBytes(original));
        Assert.Equal(
            2,
            (await journal.ListRecoveryRestoresAsync(CancellationToken.None)).Count
        );
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task StagedBlobExporter_StopsAtByteBudgetWithoutChangingIndexOrUserData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        byte[] userBytes = Enumerable.Repeat((byte)0x3C, 256 * 1024).ToArray();
        string changed = Path.Combine(opened.WorkspacePath, "oversize.bin");
        File.WriteAllBytes(changed, userBytes);
        await fixture.GitInWorkspaceAsync("add", "oversize.bin");

        Directory.CreateDirectory(fixture.SnapshotRoot);
        string rejected = Path.Combine(fixture.SnapshotRoot, "partial.bin");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitStagedBlobExporter().ExportAsync(
                opened.WorkspacePath,
                "oversize.bin",
                rejected,
                65536,
                CancellationToken.None
            )
        );

        Assert.Contains("byte budget", error.Message);
        Assert.True(File.Exists(rejected));
        Assert.InRange(new FileInfo(rejected).Length, 0, 65536);
        Assert.Equal(userBytes, File.ReadAllBytes(changed));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);

        string complete = Path.Combine(fixture.SnapshotRoot, "complete.bin");
        long length = await new GitStagedBlobExporter().ExportAsync(
            opened.WorkspacePath,
            "oversize.bin",
            complete,
            userBytes.Length,
            CancellationToken.None
        );
        Assert.Equal(userBytes.Length, length);
        Assert.Equal(userBytes, File.ReadAllBytes(complete));
        Assert.Equal(userBytes, File.ReadAllBytes(changed));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task StagedBlobExporter_RejectsNegativeBudgetBeforeAllocating()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(source, "staged value");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");

        string rejected = Path.Combine(opened.WorkspacePath, "not-written.bin");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GitStagedBlobExporter().ExportAsync(
                opened.WorkspacePath,
                "feature.txt",
                rejected,
                -1,
                CancellationToken.None
            )
        );

        Assert.False(File.Exists(rejected));
        Assert.Equal("staged value", File.ReadAllText(source));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task StagedBlobExporter_RefusesOccupiedDestinationAndAllowsNextExport()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(source, "staged and protected");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");

        Directory.CreateDirectory(fixture.SnapshotRoot);
        string occupied = Path.Combine(fixture.SnapshotRoot, "foreign.bin");
        File.WriteAllText(occupied, "foreign data");

        await Assert.ThrowsAnyAsync<IOException>(
            () => new GitStagedBlobExporter().ExportAsync(
                opened.WorkspacePath,
                "feature.txt",
                occupied,
                1024,
                CancellationToken.None
            )
        );

        Assert.Equal("foreign data", File.ReadAllText(occupied));
        string restored = Path.Combine(fixture.SnapshotRoot, "retry.bin");
        long copied = await new GitStagedBlobExporter().ExportAsync(
            opened.WorkspacePath,
            "feature.txt",
            restored,
            1024,
            CancellationToken.None
        );
        Assert.Equal(copied, new FileInfo(restored).Length);
        Assert.Equal("staged and protected", File.ReadAllText(restored));
        Assert.Equal("staged and protected", File.ReadAllText(source));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_CancellationDuringCopyPreservesOriginalAndPendingEvidence()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        byte[] userBytes = Enumerable.Repeat((byte)0x35, 256 * 1024).ToArray();
        string source = Path.Combine(opened.WorkspacePath, "cancel.bin");
        File.WriteAllBytes(source, userBytes);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.SnapshotAsync(
                copyProgress: new CancelCopyAfterBytes(
                    cancellation,
                    SnapshotCopyPhase.ExportWorkingFile,
                    65536
                ),
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(userBytes, File.ReadAllBytes(source));
        Assert.Empty(await fixture.ListPublishedSnapshotsAsync());
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.IncompleteSnapshot
        );
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_CancellationDuringCopyRetainsUnverifiedRestore()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        byte[] userBytes = Enumerable.Repeat((byte)0x76, 256 * 1024).ToArray();
        string source = Path.Combine(opened.WorkspacePath, "cancel.bin");
        File.WriteAllBytes(source, userBytes);
        WorkspaceSnapshotReceipt saved = await fixture.SnapshotAsync();
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.RestoreSnapshotAsync(
                saved.SnapshotId,
                copyProgress: new CancelCopyAfterBytes(
                    cancellation,
                    SnapshotCopyPhase.RestoreWorkingFile,
                    65536
                ),
                cancellationToken: cancellation.Token
            )
        );

        SqliteWorkspaceSessionJournal journal = new(fixture.DatabasePath);
        WorkspaceRestoreOperation interrupted = Assert.Single(
            await journal.ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, interrupted.State);
        Assert.True(await fixture.IsRegisteredWorktreeAsync(interrupted.Path));
        Assert.Equal(userBytes, File.ReadAllBytes(source));
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.InterruptedRestore
                && finding.Path == interrupted.Path
        );
        string retry = await fixture.RestoreSnapshotAsync(saved.SnapshotId);
        Assert.Equal(userBytes, File.ReadAllBytes(Path.Combine(retry, "cancel.bin")));
        Assert.Equal(userBytes, File.ReadAllBytes(source));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_VerifiedRestoreGitHeadDriftIsFlaggedWithoutMutation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(source, "never remove this");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string recovered = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == recovered
        );

        await fixture.GitInDirectoryAsync(recovered, "checkout", "--detach", "HEAD~1");

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.DivergedRecoveryWorktree
                && finding.Path == recovered
                && finding.SnapshotId == snapshot.SnapshotId
        );
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == recovered
        );
        Assert.Equal("never remove this", File.ReadAllText(source));
        Assert.Equal("never remove this", File.ReadAllText(Path.Combine(recovered, "notes.local")));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_UnexpectedFileAfterGitCheckoutIsNotOverwritten()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = Path.Combine(opened.WorkspacePath, "personal.bin");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string? occupiedPath = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RestoreSnapshotAsync(
                receipt.SnapshotId,
                new MutateAtCheckpoint(
                    SnapshotCheckpoint.RestoreWorktreeCreated,
                    () =>
                    {
                        // Simulates another process creating a conflicting file
                        // after Git reports successful recovery worktree creation.
                        string recovery = Assert.Single(
                            Directory.GetDirectories(fixture.RecoveryRoot)
                        );
                        occupiedPath = Path.Combine(recovery, "personal.bin");
                        File.WriteAllBytes(occupiedPath, [9, 8, 7, 6]);
                        return Task.CompletedTask;
                    }
                )
            )
        );

        Assert.NotNull(occupiedPath);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(occupiedPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
        WorkspaceRestoreOperation interrupted = Assert.Single(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, interrupted.State);
        Assert.Equal(occupiedPath, Path.Combine(interrupted.Path, "personal.bin"));

        string retry = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 },
            File.ReadAllBytes(Path.Combine(retry, "personal.bin")));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(occupiedPath));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Restore_LateFileCreationBetweenCopiesIsNotOverwritten()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "aaa.bin");
        File.WriteAllBytes(original, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(opened.WorkspacePath, "zzz.bin"), [4, 5, 6]);
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string? foreign = null;

        await Assert.ThrowsAnyAsync<IOException>(
            () => fixture.RestoreSnapshotAsync(
                receipt.SnapshotId,
                copyProgress: new MutateCopyOnce(
                    SnapshotCopyPhase.RestoreWorkingFile,
                    () =>
                    {
                        // First payload is aaa.bin; inject a later destination
                        // after the clean-checkout check but before zzz.bin opens.
                        string recovery = Assert.Single(
                            Directory.GetDirectories(fixture.RecoveryRoot)
                        );
                        foreign = Path.Combine(recovery, "zzz.bin");
                        File.WriteAllBytes(foreign, [9, 9, 9]);
                    }
                )
            )
        );

        Assert.NotNull(foreign);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(foreign));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
        Assert.Equal(new byte[] { 4, 5, 6 },
            File.ReadAllBytes(Path.Combine(opened.WorkspacePath, "zzz.bin")));
        WorkspaceRestoreOperation interrupted = Assert.Single(
            await new SqliteWorkspaceSessionJournal(fixture.DatabasePath)
                .ListRecoveryRestoresAsync(CancellationToken.None)
        );
        Assert.Equal(WorkspaceRestoreState.Preparing, interrupted.State);
        Assert.Equal(foreign, Path.Combine(interrupted.Path, "zzz.bin"));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_NewIgnoredFileMakesVerifiedDirectoryDiverged()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, ".gitignore"), "generated/\n");
        string generated = Path.Combine(opened.WorkspacePath, "generated");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "original.bin"), "protected");

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );

        string foreign = Path.Combine(restored, "generated", "new.cache");
        File.WriteAllText(foreign, "new ignored data");
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.DivergedRecoveryWorktree
                && finding.Path == restored
        );
        Assert.True(File.Exists(foreign));
        Assert.Equal("protected", File.ReadAllText(
            Path.Combine(generated, "original.bin")
        ));
        Assert.Equal("protected", File.ReadAllText(
            Path.Combine(restored, "generated", "original.bin")
        ));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_ChangedIgnoredBinaryIsFlaggedEvenIfGitStatusDoesNotChange()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string ignore = Path.Combine(opened.WorkspacePath, ".gitignore");
        File.WriteAllText(ignore, "generated/\n");
        string ignored = Path.Combine(opened.WorkspacePath, "generated", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(ignored)!);
        File.WriteAllBytes(ignored, [10, 20, 30, 40]);

        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            item => item.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && item.Path == restored
        );

        string restoredIgnored = Path.Combine(restored, "generated", "cache.bin");
        File.WriteAllBytes(restoredIgnored, [10, 21, 30, 40]);
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            item => item.Kind == WorkspaceRecoveryKind.DivergedRecoveryWorktree
                && item.Path == restored
        );
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, File.ReadAllBytes(ignored));
        Assert.Equal(new byte[] { 10, 21, 30, 40 }, File.ReadAllBytes(restoredIgnored));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_ChangedGitIndexIsFlaggedWithoutChangingOriginal()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "local edits");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);

        File.WriteAllText(Path.Combine(restored, "feature.txt"), "different staged value");
        await fixture.GitInDirectoryAsync(restored, "add", "feature.txt");

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            item => item.Kind == WorkspaceRecoveryKind.DivergedRecoveryWorktree
                && item.Path == restored
        );
        Assert.Equal("local edits", File.ReadAllText(original));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_CorruptStoredStagedBlobIsUnverifiableWithoutChangingSource()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(original, "staged value");
        await fixture.GitInWorkspaceAsync("add", "feature.txt");
        File.WriteAllText(original, "unstaged value");

        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);
        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );

        string storedStaged = Path.Combine(snapshot.Folder, "staged", "feature.txt");
        File.WriteAllText(storedStaged, "STAGED VALUE");
        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.UnverifiableRecoveryDirectory
                && finding.Path == restored
        );
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );
        Assert.Equal("unstaged value", File.ReadAllText(original));
        Assert.Equal("unstaged value", File.ReadAllText(Path.Combine(restored, "feature.txt")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_DuplicateRecordedFilesAreUnverifiableDespiteMatchingHashes()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        File.WriteAllText(Path.Combine(opened.WorkspacePath, ".gitignore"), "generated/\n");
        string dir = Path.Combine(opened.WorkspacePath, "generated");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.bin"), "same payload");
        File.WriteAllText(Path.Combine(dir, "b.bin"), "same payload");

        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);
        string manifestPath = Path.Combine(snapshot.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        int target = manifest.Files.FindIndex(file => file.RelativePath == "generated/b.bin");
        Assert.True(target >= 0);
        manifest.Files[target] = manifest.Files[target] with { RelativePath = "generated/a.bin" };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.UnverifiableRecoveryDirectory
                && finding.Path == restored
        );
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );
        Assert.Equal("same payload", File.ReadAllText(Path.Combine(dir, "a.bin")));
        Assert.Equal("same payload", File.ReadAllText(Path.Combine(dir, "b.bin")));
        Assert.Equal("same payload", File.ReadAllText(Path.Combine(restored, "generated", "b.bin")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_ManifestByteCountDriftIsUnverifiable()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string source = Path.Combine(opened.WorkspacePath, "note.local");
        File.WriteAllText(source, "important user content");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);

        string manifestPath = Path.Combine(snapshot.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        int file = manifest.Files.FindIndex(item => !item.IsDirectory);
        Assert.True(file >= 0);
        manifest.Files[file] = manifest.Files[file] with { Length = manifest.Files[file].Length + 1 };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        Assert.Contains(
            await fixture.ScanRecoveryAsync(),
            finding => finding.Kind == WorkspaceRecoveryKind.UnverifiableRecoveryDirectory
                && finding.Path == restored
        );
        Assert.Equal("important user content", File.ReadAllText(source));
        Assert.Equal("important user content", File.ReadAllText(Path.Combine(restored, "note.local")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_ReducedManifestFileListIsUnverifiableWithoutChangingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(original, "keep original");
        WorkspaceSnapshotReceipt receipt = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(receipt.SnapshotId);

        string manifestPath = Path.Combine(receipt.Folder, "manifest.json");
        WorkspaceSnapshotManifest manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
            File.ReadAllText(manifestPath)
        )!;
        Assert.NotEmpty(manifest.Files);
        manifest.Files.Clear();
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.UnverifiableRecoveryDirectory
                && finding.Path == restored
        );
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );
        Assert.Equal("keep original", File.ReadAllText(original));
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(restored, "notes.local")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Recovery_MissingManifestMakesVerifiedRestoreUnverifiableWithoutDeletion()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string original = Path.Combine(opened.WorkspacePath, "notes.local");
        File.WriteAllText(original, "keep all data");
        WorkspaceSnapshotReceipt snapshot = await fixture.SnapshotAsync();
        string restored = await fixture.RestoreSnapshotAsync(snapshot.SnapshotId);

        string manifest = Path.Combine(snapshot.Folder, "manifest.json");
        string savedManifest = manifest + ".moved";
        File.Move(manifest, savedManifest);

        IReadOnlyList<WorkspaceRecoveryFinding> findings = await fixture.ScanRecoveryAsync();
        Assert.Contains(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.UnverifiableRecoveryDirectory
                && finding.Path == restored
        );
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                && finding.Path == restored
        );
        Assert.True(File.Exists(savedManifest));
        Assert.Equal("keep all data", File.ReadAllText(original));
        Assert.Equal("keep all data", File.ReadAllText(Path.Combine(restored, "notes.local")));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task Snapshot_RefusesLockedUserFileWithoutPublishing()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        OpenPullRequestResult opened = await fixture.OpenAsync(PullRequestUrl);
        string changedFile = Path.Combine(opened.WorkspacePath, "feature.txt");
        File.WriteAllText(changedFile, "important work");

        using (FileStream held = new(
            changedFile,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        ))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.SnapshotAsync());
        }

        Assert.Equal("important work", File.ReadAllText(changedFile));
        Assert.Equal(WorkspaceSessionState.Active, (await fixture.OwnedSessionAsync()).State);
        Assert.Empty(Directory.GetDirectories(fixture.SnapshotRoot, "snapshot-*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("Category", "FastE2E")]
    public async Task OpenPullRequest_UnknownPullRequestDoesNotAllocateWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.OpenAsync("https://github.com/owner/repo/pull/999")
        );

        Assert.False(Directory.Exists(fixture.WorkspacePath(999)));
        Assert.Empty(fixture.Editor.OpenedPaths);
    }

    private sealed class MutateCopyOnce(
        SnapshotCopyPhase target,
        Action mutation
    ) : ISnapshotCopyProgressSink
    {
        private bool _mutated;

        public ValueTask AfterBytesWrittenAsync(
            SnapshotCopyPhase phase,
            long bytesWritten,
            CancellationToken cancellationToken
        )
        {
            if (!_mutated && phase == target)
            {
                _mutated = true;
                mutation();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelCopyAfterBytes(
        CancellationTokenSource source,
        SnapshotCopyPhase target,
        long cancelAfter
    ) : ISnapshotCopyProgressSink
    {
        public ValueTask AfterBytesWrittenAsync(
            SnapshotCopyPhase phase,
            long bytesWritten,
            CancellationToken cancellationToken
        )
        {
            if (phase == target && bytesWritten >= cancelAfter)
            {
                source.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailCopyAfterBytes(
        SnapshotCopyPhase target,
        long failAfter
    ) : ISnapshotCopyProgressSink
    {
        public ValueTask AfterBytesWrittenAsync(
            SnapshotCopyPhase phase,
            long bytesWritten,
            CancellationToken cancellationToken
        )
        {
            if (phase == target && bytesWritten >= failAfter)
            {
                throw new IOException("Injected disk full during snapshot file copy.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedDiskSpaceProbe(long bytes) : ISnapshotDiskSpaceProbe
    {
        public long AvailableBytes(string targetPath) => bytes;
    }

    private sealed class MutateAtCheckpoint(
        SnapshotCheckpoint expected,
        Func<Task> mutation
    ) : ISnapshotCheckpointSink
    {
        public async ValueTask OnCheckpointAsync(
            SnapshotCheckpoint checkpoint,
            string operationPath,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint == expected)
            {
                await mutation();
            }
        }
    }

    private sealed class ThrowAtParkingCheckpoint(
        WorkspaceParkingCheckpoint expected
    ) : IWorkspaceParkingCheckpointSink
    {
        public ValueTask AfterAsync(
            WorkspaceParkingCheckpoint checkpoint,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint == expected)
            {
                throw new InvalidOperationException("Injected parking failure at " + checkpoint);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowAtCheckpoint(SnapshotCheckpoint expected) : ISnapshotCheckpointSink
    {
        public ValueTask OnCheckpointAsync(
            SnapshotCheckpoint checkpoint,
            string operationPath,
            CancellationToken cancellationToken
        )
        {
            if (checkpoint == expected)
            {
                throw new InvalidOperationException(
                    "Injected fault at " + checkpoint
                );
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _source;
        private readonly string _workspaces;
        private readonly OpenPullRequestHandler _handler;

        private Fixture(string root, string source, string workspaces, FakeEditor editor)
        {
            _root = root;
            _source = source;
            _workspaces = workspaces;
            Editor = editor;

            _handler = CreateHandler();

        }

        public string DatabasePath => Path.Combine(_root, "state", "sessions.db");

        public string SignalRoot => _root;

        public string SnapshotRoot => Path.Combine(_root, "snapshots");

        public string RecoveryRoot => Path.Combine(_root, "recovered");

        public string SourcePath => _source;

        public FakeEditor Editor { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "git-workspace-pool-e2e-" + Guid.NewGuid().ToString("N")
            );
            string remote = Path.Combine(root, "remote.git");
            string seed = Path.Combine(root, "seed");
            string source = Path.Combine(root, "source");
            string workspaces = Path.Combine(root, "workspaces");

            Directory.CreateDirectory(root);

            try
            {
                await GitAsync(root, "init", "--bare", "--initial-branch=main", remote);
                await GitAsync(root, "init", "--initial-branch=main", seed);
                await GitAsync(seed, "config", "user.name", "E2E Fixture");
                await GitAsync(seed, "config", "user.email", "test@example.invalid");

                File.WriteAllText(Path.Combine(seed, "README.md"), "base");
                await GitAsync(seed, "add", "README.md");
                await GitAsync(seed, "commit", "-m", "base");
                await GitAsync(seed, "remote", "add", "origin", remote);
                await GitAsync(seed, "push", "origin", "HEAD:refs/heads/main");

                await GitAsync(seed, "checkout", "-b", "feature/pr-17");
                File.WriteAllText(Path.Combine(seed, "feature.txt"), "feature for pr 17");
                await GitAsync(seed, "add", "feature.txt");
                await GitAsync(seed, "commit", "-m", "PR 17");
                await GitAsync(seed, "push", "origin", "HEAD:refs/pull/17/head");
                await GitAsync(root, "clone", "--no-local", remote, source);

                return new Fixture(root, source, workspaces, new FakeEditor());
            }
            catch
            {
                DeleteFixtureTree(root);
                throw;
            }
        }

        public async Task CreateUnjournalledWorktreeAsync()
        {
            await GitAsync(_source, "fetch", "--no-tags", "origin", "refs/pull/17/head");
            Directory.CreateDirectory(_workspaces);
            await GitAsync(
                _source,
                "worktree",
                "add",
                "--detach",
                WorkspacePath(17),
                "FETCH_HEAD"
            );
        }

        public async Task ConfigureDangerousFilterAsync()
        {
            // No malicious command is executed; a configured external process
            // is sufficient to prove checkout must be refused.
            await GitAsync(_source, "config", "filter.untrusted.smudge", "should-not-run");
            await GitAsync(_source, "config", "filter.untrusted.process", "should-not-run");
        }

        public async Task ConfigureFailingCheckoutHookAsync()
        {
            string hooks = Path.Combine(_root, "untrusted-hooks");
            Directory.CreateDirectory(hooks);
            File.WriteAllText(
                Path.Combine(hooks, "post-checkout"),
                "#!/bin/sh\nexit 91\n"
            );
            await GitAsync(_source, "config", "core.hooksPath", hooks);
        }

        public Task GitInWorkspaceAsync(params string[] arguments) =>
            GitAsync(WorkspacePath(17), arguments);

        public Task GitInDirectoryAsync(string path, params string[] arguments) =>
            GitAsync(path, arguments);

        public async Task<string> ReadStagedFeatureAsync(string? worktreePath = null)
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = worktreePath ?? WorkspacePath(17),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add(":feature.txt");
            using Process process = Process.Start(startInfo)!;
            string result = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return result.TrimEnd('\r', '\n');
        }

        public async Task<WorkspaceSession> OwnedSessionAsync()
        {
            SqliteWorkspaceSessionJournal journal = new(
                Path.Combine(_root, "state", "sessions.db")
            );
            WorkspaceSession? session = Assert.Single(
                await journal.ListActiveAsync(CancellationToken.None)
            );
            return session;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryFinding>> ScanRecoveryAsync()
        {
            SqliteWorkspaceSessionJournal sessions = new(DatabasePath);
            WorkspaceRecoveryInspector inspector = new(
                SnapshotRoot,
                RecoveryRoot,
                sessions,
                new GitCliWorkspaceGit(),
                new SqliteWorkspaceSlotRegistry(DatabasePath)
            );
            return inspector.ScanAsync(CancellationToken.None);
        }

        public Task<WorkspaceSession?> SessionAsync(Guid operationId) =>
            new SqliteWorkspaceSessionJournal(DatabasePath).FindAsync(
                operationId,
                CancellationToken.None
            );

        public Task<IReadOnlyList<WorkspaceSession>> ActiveSessionsAsync() =>
            new SqliteWorkspaceSessionJournal(DatabasePath).ListActiveAsync(
                CancellationToken.None
            );

        public async Task<string> SuspendAsync(Guid operationId)
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            SuspendWorkspaceSessionService service = new(
                journal,
                journal,
                new FileWorkspaceOperationLock(
                    Path.Combine(_root, "state", "locks")
                ),
                new GitCliWorkspaceGit(),
                new WorkspaceDirectories()
            );
            return await service.SuspendAsync(operationId, CancellationToken.None);
        }

        public Task<string> OpenRetainedReleasedAsync(Guid sessionId)
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            OpenReleasedParkedWorkspaceService service = new(
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks")),
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit()),
                Editor
            );
            return service.OpenAsync(sessionId, CancellationToken.None);
        }

        public Task<WorkspaceSlotReleasePreparation> PrepareReleaseAsync(Guid operationId)
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            FileWorkspaceOperationLock locks = new(Path.Combine(_root, "state", "locks"));
            SqliteWorkspaceSlotRegistry slots = new(DatabasePath);
            AssessWorkspaceReleaseService preflight = new(
                journal,
                slots,
                locks,
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit())
            );
            PrepareWorkspaceSlotReleaseService preparation = new(
                journal,
                locks,
                preflight,
                slots
            );
            return preparation.PrepareAsync(operationId, CancellationToken.None);
        }

        public Task<WorkspaceSlotReleasePreparation> FinalizeReleaseAsync(Guid preparationId)
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            FileWorkspaceOperationLock locks = new(Path.Combine(_root, "state", "locks"));
            SqliteWorkspaceSlotRegistry slots = new(DatabasePath);
            AssessWorkspaceReleaseService preflight = new(
                journal,
                slots,
                locks,
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit())
            );
            FinalizeWorkspaceSlotReleaseService finalizer = new(
                journal,
                locks,
                preflight,
                slots
            );
            return finalizer.FinalizeAsync(preparationId, CancellationToken.None);
        }

        public Task<WorkspaceReleaseAssessment> AssessReleaseAsync(Guid operationId)
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            AssessWorkspaceReleaseService inspector = new(
                journal,
                new SqliteWorkspaceSlotRegistry(DatabasePath),
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks")),
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit())
            );
            return inspector.AssessAsync(operationId, CancellationToken.None);
        }

        public Task<string> ParkAsync(
            Guid operationId,
            IWorkspaceParkingCheckpointSink? checkpoint = null
        )
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            ParkWorkspaceSessionService service = new(
                journal,
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks")),
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit()),
                checkpoint
            );
            return service.ParkAsync(operationId, CancellationToken.None);
        }

        public Task<string> UnparkAsync(
            Guid operationId,
            IWorkspaceParkingCheckpointSink? checkpoint = null
        )
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            ParkWorkspaceSessionService service = new(
                journal,
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks")),
                new GitWorkspaceParkingOperations(new GitCliWorkspaceGit()),
                checkpoint
            );
            return service.UnparkAsync(operationId, CancellationToken.None);
        }

        public Task<IReadOnlyList<WorkspaceSnapshotRecord>> ListPublishedSnapshotsAsync()
        {
            SqliteWorkspaceSessionJournal sessions = new(
                Path.Combine(_root, "state", "sessions.db")
            );
            return sessions.ListSnapshotsAsync(CancellationToken.None);
        }

        public async Task<WorkspaceSnapshotReceipt> SnapshotAsync(
            ISnapshotCheckpointSink? checkpoints = null,
            ISnapshotDiskSpaceProbe? diskSpace = null,
            ISnapshotCopyProgressSink? copyProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            WorkspaceSession session = await OwnedSessionAsync();
            string state = Path.Combine(_root, "state");
            CaptureWorkspaceSnapshotService handler = new(
                new SqliteWorkspaceSessionJournal(Path.Combine(state, "sessions.db")),
                new FileWorkspaceOperationLock(Path.Combine(state, "locks")),
                new GitCliWorkspaceGit(),
                new VerifiedWorkspaceSnapshotExporter(
                    SnapshotRoot,
                    new GitWorktreeSnapshotScanner(),
                    checkpoints,
                    diskSpace,
                    copyProgress
                )
            );
            return await handler.CaptureAsync(session.OperationId, cancellationToken);
        }

        public async Task<string> RestoreSnapshotAsync(
            Guid snapshotId,
            ISnapshotCheckpointSink? checkpoints = null,
            ISnapshotDiskSpaceProbe? diskSpace = null,
            ISnapshotCopyProgressSink? copyProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            SqliteWorkspaceSessionJournal sessions = new(
                Path.Combine(_root, "state", "sessions.db")
            );
            RestoreWorkspaceSnapshotService service = new(
                sessions,
                new VerifiedWorkspaceSnapshotRestorer(
                    RecoveryRoot,
                    new GitCliWorkspaceGit(),
                    new GitWorktreeSnapshotScanner(),
                    sessions,
                    checkpoints,
                    diskSpace,
                    copyProgress
                )
            );
            return await service.RestoreAsync(snapshotId, cancellationToken);
        }

        public async Task CreatePullRequestAsync(int number)
        {
            string seed = Path.Combine(_root, "seed");
            await GitAsync(seed, "checkout", "main");
            await GitAsync(seed, "checkout", "-b", "feature/pr-" + number);
            File.WriteAllText(Path.Combine(seed, "feature.txt"), "feature for pr " + number);
            await GitAsync(seed, "add", "feature.txt");
            await GitAsync(seed, "commit", "-m", "PR " + number);
            await GitAsync(seed, "push", "origin", "HEAD:refs/pull/" + number + "/head");
        }

        public Task<OpenPullRequestResult> OpenInReadySlotAsync(
            string url,
            Guid slotId,
            long generation
        )
        {
            Assert.True(PullRequestReference.TryParse(url, out PullRequestReference? request));
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            SqliteWorkspaceSlotRegistry slots = new(DatabasePath);
            AcquireExplicitReadySlotService service = new(
                slots,
                slots,
                new WorkspaceReadySlotSafety(),
                new GitCliWorkspaceGit(),
                new WorkspaceDirectories(),
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks"))
            );
            return service.AcquireAsync(
                request!,
                new RepositoryRegistration("owner/repo", _source, _workspaces),
                slotId,
                generation,
                CancellationToken.None
            );
        }

        public Task<OpenPullRequestResult> OpenInReadySlotWithHandlerAsync(
            string url,
            Guid slotId,
            long generation
        )
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            SqliteWorkspaceSlotRegistry slots = new(DatabasePath);
            AcquireExplicitReadySlotService acquisition = new(
                slots,
                slots,
                new WorkspaceReadySlotSafety(),
                new GitCliWorkspaceGit(),
                new WorkspaceDirectories(),
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks"))
            );
            OpenPullRequestInReadySlotHandler handler = new(
                new Catalog(new RepositoryRegistration("owner/repo", _source, _workspaces)),
                new LocalFixtureOriginVerifier(),
                acquisition,
                Editor
            );
            return handler.HandleAsync(url, slotId, generation, CancellationToken.None);
        }

        public Task<WarmArtifactTransferReceipt> TransferNpmAsync(
            Guid sourceId,
            Guid targetId
        )
        {
            SqliteWorkspaceSessionJournal journal = new(DatabasePath);
            TransferVerifiedWarmArtifactsService service = new(
                journal,
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks")),
                new GitCliWorkspaceGit(),
                new VerifiedNpmArtifactCopier()
            );
            return service.TransferAsync(sourceId, targetId, CancellationToken.None);
        }

        public Task<WorkspaceSession> ReconcileSelectedReadySlotAsync(
            WorkspaceReadySlotReservation selected
        )
        {
            ReconcileExplicitReadySlotService recovery = NewReconcileService();
            return recovery.ReconcileSelectedAsync(
                selected,
                new RepositoryRegistration("owner/repo", _source, _workspaces),
                CancellationToken.None
            );
        }

        public Task<WorkspaceSession> ReconcileReadySlotAsync(Guid slotId)
        {
            ReconcileExplicitReadySlotService recovery = NewReconcileService();
            return recovery.ReconcileAsync(
                slotId,
                new RepositoryRegistration("owner/repo", _source, _workspaces),
                CancellationToken.None
            );
        }

        private ReconcileExplicitReadySlotService NewReconcileService()
        {
            SqliteWorkspaceSlotRegistry slots = new(DatabasePath);
            return new ReconcileExplicitReadySlotService(
                slots,
                slots,
                new WorkspaceReadySlotSafety(),
                new VerifiedReadySlotCheckout(),
                new GitCliWorkspaceGit(),
                new FileWorkspaceOperationLock(Path.Combine(_root, "state", "locks"))
            );
        }

        public Task<OpenPullRequestResult> OpenAsync(string url) =>
            _handler.HandleAsync(url, CancellationToken.None);

        public Task<OpenPullRequestResult> OpenWithFreshHandlerAsync(string url) =>
            CreateHandler().HandleAsync(url, CancellationToken.None);

        public async Task SimulateInterruptedPreparationAsync(int pullRequestNumber)
        {
            string statePath = Path.Combine(_root, "state", "sessions.db");
            SqliteWorkspaceSessionJournal journal = new(statePath);
            WorkspaceSession session = await journal.GetOrCreateAsync(
                "github:owner/repo",
                pullRequestNumber,
                _source,
                WorkspacePath(pullRequestNumber),
                CancellationToken.None
            );
            GitCliWorkspaceGit git = new();
            await git.FetchPullRequestAsync(_source, pullRequestNumber, CancellationToken.None);
            string pinned = await git.GetPullRequestCommitAsync(
                _source,
                pullRequestNumber,
                CancellationToken.None
            );
            await journal.PinCommitAsync(session.OperationId, pinned, CancellationToken.None);
            Directory.CreateDirectory(_workspaces);
            await git.CreateWorktreeAsync(
                _source,
                WorkspacePath(pullRequestNumber),
                pinned,
                CancellationToken.None
            );

            // Simulate process termination before the journal's Activated transition.
        }

        private OpenPullRequestHandler CreateHandler()
        {
            RepositoryRegistration registration = new("owner/repo", _source, _workspaces);
            string stateDirectory = Path.Combine(_root, "state");
            SqliteWorkspaceSessionJournal journal = new(
                Path.Combine(stateDirectory, "sessions.db")
            );
            FileWorkspaceOperationLock locks = new(Path.Combine(stateDirectory, "locks"));
            GitCliWorkspaceGit git = new();
            WorkspaceAcquisitionService acquisition = new(
                git, new WorkspaceDirectories(), journal, locks
            );
            OpenReleasedParkedWorkspaceService retained = new(
                journal, locks, new GitWorkspaceParkingOperations(git), Editor
            );
            return new OpenPullRequestHandler(
                new Catalog(registration),
                new LocalFixtureOriginVerifier(),
                acquisition,
                Editor,
                journal,
                retained
            );
        }

        public string WorkspacePath(int number) => Path.Combine(
            _workspaces,
            $"github-owner-repo-pr-{number}"
        );

        public Task<bool> IsRegisteredWorktreeAsync(string path) =>
            new GitCliWorkspaceGit().IsRegisteredWorktreeAsync(
                _source,
                path,
                CancellationToken.None
            );

        public void Dispose()
        {
            DeleteFixtureTree(_root);
        }

        private static void DeleteFixtureTree(string root)
        {
            // Git object files are read-only on Windows. This is disposable test data only.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }

        private static async Task GitAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Git is not installed.");

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            string stderr = await error;
            _ = await output;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git {string.Join(' ', arguments)} failed: {stderr}"
                );
            }
        }
    }

    private sealed class LocalFixtureOriginVerifier : IRepositoryOriginVerifier
    {
        public Task VerifyAsync(
            PullRequestReference request,
            RepositoryRegistration registration,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class Catalog(RepositoryRegistration registration) : IRepositoryCatalog
    {
        public RepositoryRegistration? Find(PullRequestReference request) =>
            request.RepositoryKey.Equals(registration.RepositoryKey, StringComparison.OrdinalIgnoreCase)
                ? registration
                : null;
    }

    private sealed class FakeEditor : IEditorLauncher
    {
        private readonly ConcurrentQueue<string> _openedPaths = new();

        public IReadOnlyCollection<string> OpenedPaths => _openedPaths.ToArray();

        public Task OpenAsync(string workspacePath, CancellationToken cancellationToken)
        {
            _openedPaths.Enqueue(workspacePath);
            return Task.CompletedTask;
        }
    }
}
