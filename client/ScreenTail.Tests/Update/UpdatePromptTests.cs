using ScreenTail.Core.Notifications;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Update;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Update;

/// <summary>
/// What the technician is shown (ST-113 AC1 and AC3): an update announced only when idle, once per
/// version; the channel theirs to choose unless the admin chose it; Install only when it can work.
/// </summary>
public sealed class UpdatePromptTests
{
    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public void NothingIsSaidAboutAnUpdateDuringASession(string state)
    {
        Assert.Null(UpdateNotices.For(Capture(state), Ready()));
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftFailed)]
    public void AReadyUpdateIsAnnouncedWhenNoSessionIsRunning(string state)
    {
        var notice = UpdateNotices.For(Capture(state), Ready());

        Assert.NotNull(notice);
        // The label-then-detail form the other notices take since #228 ("Draft ready: ..."), no trailing
        // full stop on the label, sentence case, nothing exclaimed.
        Assert.Equal("Update ready: ScreenTail 0.1.20. Open Settings, then Updates, to install it", notice.Text);
        Assert.Equal("update-ready:0.1.20", notice.Key);
    }

    [Theory]
    [InlineData("available")]
    [InlineData("downloading")]
    [InlineData("refused")]
    [InlineData("failed")]
    [InlineData("up_to_date")]
    [InlineData("installing")]
    public void NothingElseAboutUpdatesIsAToast(string state)
    {
        Assert.Null(UpdateNotices.For(Capture(CaptureStates.Idle), Ready() with { State = state }));
    }

    [Fact]
    public void WithoutAnAnswerFromTheServiceNothingIsSaid()
    {
        Assert.Null(UpdateNotices.For(null, Ready()));
        Assert.Null(UpdateNotices.For(Capture(CaptureStates.Idle), null));
    }

    [Fact]
    public async Task TheChannelIsTheTechniciansToChooseUntilTheAdminChoosesIt()
    {
        var gateway = new FakeGateway(Snapshot(locked: false));
        var panel = new UpdatesPanel(gateway);
        await panel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(panel.CanChooseChannel);
        Assert.Null(panel.ChannelNote);
        Assert.True(await panel.ChooseAsync(UpdateChannel.Pilot, TestContext.Current.CancellationToken));
        Assert.Equal([UpdateChannel.Pilot], gateway.Chosen);

        gateway.Next = Snapshot(locked: true);
        await panel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(panel.CanChooseChannel);
        Assert.Equal(UpdatesPanel.LockedNote, panel.ChannelNote);
        Assert.False(await panel.ChooseAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken));
        Assert.Single(gateway.Chosen);
    }

    [Fact]
    public async Task InstallIsOfferedOnlyWhenTheServiceSaysItCanWork()
    {
        var gateway = new FakeGateway(Snapshot(canInstall: false));
        var panel = new UpdatesPanel(gateway);
        await panel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(panel.CanInstall);
        Assert.False(await panel.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, gateway.Installs);

        gateway.Next = Snapshot(canInstall: true);
        await panel.LoadAsync(TestContext.Current.CancellationToken);
        gateway.Refusal = UpdateInstaller.DuringSession;

        Assert.False(await panel.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, gateway.Installs);
        Assert.Equal(UpdateInstaller.DuringSession, panel.Problem);
    }

    [Fact]
    public async Task AServiceThatDoesNotAnswerOffersNothing()
    {
        var panel = new UpdatesPanel(new FakeGateway(null));

        await panel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(panel.Answered);
        Assert.False(panel.CanInstall);
        Assert.False(panel.CanChooseChannel);
    }

    [Fact]
    public void TheReportOverThePipeReadsBackIntoTheCard()
    {
        var snapshot = PipeUpdates.Snapshot(Ready() with { Channel = "pilot", ChannelLocked = true });

        Assert.Equal(UpdateChannel.Pilot, snapshot.Channel);
        Assert.True(snapshot.ChannelLocked);
        Assert.True(snapshot.CanInstall);
        Assert.Equal("0.1.20", snapshot.AvailableVersion);
    }

    private static CaptureStateSnapshot Capture(string state) => new() { State = state };

    private static UpdateReported Ready() => new()
    {
        State = "ready",
        InstalledVersion = "0.1.10",
        AvailableVersion = "0.1.20",
        Channel = "stable",
        ChannelLocked = false,
        Sentence = "ScreenTail 0.1.20 is ready to install.",
        CanInstall = true,
    };

    private static UpdateSnapshot Snapshot(bool locked = false, bool canInstall = false) =>
        new("ready", "0.1.10", "0.1.20", UpdateChannel.Stable, locked, "ScreenTail 0.1.20 is ready to install.", canInstall);

    private sealed class FakeGateway(UpdateSnapshot? first) : IUpdateGateway
    {
        public UpdateSnapshot? Next { get; set; } = first;

        public List<UpdateChannel> Chosen { get; } = [];

        public int Installs { get; private set; }

        public string? Refusal { get; set; }

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Next);

        public Task<string?> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default)
        {
            Chosen.Add(channel);
            Next = Next! with { Channel = channel };
            return Task.FromResult<string?>(null);
        }

        public Task<string?> InstallAsync(CancellationToken ct = default)
        {
            Installs++;
            return Task.FromResult(Refusal);
        }
    }
}
