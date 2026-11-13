using System.Security.Cryptography;
using System.Text.Json;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Store;

/// <summary>
/// Spec v0.6.9 §5 S4: what a rail row says beside the tool. The note's own title (the draft's
/// <c>suggested_title</c>, never a window title), the ticket the session went to or, before that, the
/// digits read off the window at start, and the company of the ticket it was published to. The company is
/// stored on the device only once a publish may have reached that ticket; a discard takes all three.
/// </summary>
public sealed class SessionLabelsStoreTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 14, 2, 0, TimeSpan.FromHours(-5));
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKeyProvider _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SqliteSessionStore> _stores = [];

    [Fact]
    public async Task ASessionWithNothingKnownHasNoLabels()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));

        var row = Assert.Single(await store.ListSessionsAsync());
        Assert.Null(row.Title);
        Assert.Null(row.Ticket);
        Assert.Null(row.Company);
    }

    [Fact]
    public async Task TheTicketReadOffTheWindowIsTheRowsTicketUntilAPublishNamesOne()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1") with { SuggestedTicket = "48213" });
        await store.SaveDraftAsync("s1", Note("Printer offline"));

        var before = Assert.Single(await store.ListSessionsAsync());
        Assert.Equal("Printer offline", before.Title);
        Assert.Equal("48213", before.Ticket);
        Assert.Null(before.Company);

        Assert.True(await store.SavePublishedTicketAsync("s1", "50117", "Acme Dental", TestContext.Current.CancellationToken));

        var after = (await store.GetSessionSummaryAsync("s1"))!;
        Assert.Equal("50117", after.Ticket);
        Assert.Equal("Acme Dental", after.Company);
    }

    [Fact]
    public async Task ABlankCompanyIsStoredAsNone()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));

        Assert.True(await store.SavePublishedTicketAsync("s1", "50117", "  ", TestContext.Current.CancellationToken));

        var row = (await store.GetSessionSummaryAsync("s1"))!;
        Assert.Equal("50117", row.Ticket);
        Assert.Null(row.Company);
    }

    [Fact]
    public async Task ADiscardTakesTheTitleTheTicketAndTheCompany()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1") with { SuggestedTicket = "48213" });
        await store.SaveDraftAsync("s1", Note("Printer offline"));
        Assert.True(await store.SavePublishedTicketAsync("s1", "50117", "Acme Dental", TestContext.Current.CancellationToken));

        _ = await store.DiscardSessionsAsync(["s1"], TestContext.Current.CancellationToken);

        var row = (await store.GetSessionSummaryAsync("s1"))!;
        Assert.Null(row.Title);
        Assert.Null(row.Ticket);
        Assert.Null(row.Company);
    }

    [Fact]
    public async Task ADiscardedSessionIsNotGivenATicketAfterwards()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        _ = await store.DiscardSessionsAsync(["s1"], TestContext.Current.CancellationToken);

        Assert.False(await store.SavePublishedTicketAsync("s1", "50117", "Acme Dental", TestContext.Current.CancellationToken));
        Assert.False(await store.SavePublishedTicketAsync("missing", "50117", "Acme Dental", TestContext.Current.CancellationToken));
        Assert.Null((await store.GetSessionSummaryAsync("s1"))!.Company);
    }

    [Fact]
    public async Task TheRowsLabelsCrossThePipeAndARowWithoutThemCarriesNoMember()
    {
        // Contract v11: optional members, left off when unknown, so a row without them is the v10 row.
        var labelled = new SessionRow { Id = "s1", StartedAt = At, DurationMs = 1, Status = "draft", Frames = 0, FramesPurged = 0, Title = "Printer offline", Ticket = "48213", Company = "Acme Dental" };
        var bare = new SessionRow { Id = "s2", StartedAt = At, DurationMs = 1, Status = "draft", Frames = 0, FramesPurged = 0 };
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync<IpcEvent>(stream, new SessionsListed { RequestId = 3, Sessions = [labelled, bare] }, TestContext.Current.CancellationToken);
        var wire = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;

        var listed = Assert.IsType<SessionsListed>(await IpcFraming.ReadAsync<IpcEvent>(stream, TestContext.Current.CancellationToken));

        Assert.Equal(labelled, listed.Sessions[0]);
        Assert.Equal(bare, listed.Sessions[1]);
        Assert.Contains("\"title\":\"Printer offline\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"ticket\":\"48213\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"company\":\"Acme Dental\"", wire, StringComparison.Ordinal);
        var second = JsonDocument.Parse(JsonSerializer.Serialize(bare, IpcFraming.Json)).RootElement;
        Assert.False(second.TryGetProperty("title", out _));
        Assert.False(second.TryGetProperty("ticket", out _));
        Assert.False(second.TryGetProperty("company", out _));
        Assert.Equal(11, IpcContract.Version);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private async Task<SqliteSessionStore> OpenAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, _key);
        _stores.Add(store);
        return store;
    }

    private static NewSession Session(string id) => new(id, At, new RemoteTool { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" }, LocalOnly: false, PolicyVersion: "v14");

    private static DraftNote Note(string title) => new()
    {
        Problem = "Nothing printed.",
        Steps = [new DraftStep { Text = "Started the spooler.", Confidence = StepConfidence.High, FrameRefs = [] }],
        Result = "It prints.",
        FollowUps = [],
        SuggestedTitle = title,
        SuggestedTimeMinutes = 30,
        KbCandidate = false,
        KbReason = "one-off",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };

    private sealed class FixedKeyProvider(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
