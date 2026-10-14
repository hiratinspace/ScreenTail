using System.Security.Cryptography;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-041 against the real store. The claim being tested is INV-1 itself: a frame this worker marks
/// readable has had its secrets painted over first, and a frame it could not check is gone rather than
/// stored.
/// </summary>
public sealed class RedactionWorkerTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 11, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task AFrameWithACardNumberIsStoredWithItCoveredOver()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, Reads(
            new OcrWord("Card", 10, 40, 50, 20),
            new OcrWord("4111111111111111", 70, 40, 200, 20)));

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        var session = (await store.LoadSessionAsync("s1"))!;
        var frame = Assert.Single(session.Frames);
        Assert.False(frame.RedactionPending);
        Assert.NotNull(frame.RedactedAt);
        Assert.Equal("Card [CARD]", frame.OcrText);
        var region = Assert.Single(frame.MaskedRegions);
        Assert.Equal(MaskKind.Card, region.Kind);
        Assert.Equal(70, region.X);
    }

    [Fact]
    public async Task TheStoredTextNeverContainsWhatWasMasked()
    {
        // INV-1 and INV-10 together: the frame is readable afterwards, and nothing kept alongside it
        // repeats the secret that was covered.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, Reads(
            new OcrWord("password", 0, 0, 80, 20),
            new OcrWord("is", 90, 0, 20, 20),
            new OcrWord("Winter2026", 120, 0, 120, 20)));

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        var frame = Assert.Single((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.DoesNotContain("Winter2026", frame.OcrText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", frame.OcrText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrameTheRecogniserCouldNotReadIsDeletedRatherThanStored()
    {
        // The whole argument for this behaviour: a frame nobody could read is the likeliest place for a
        // secret to survive. It is dropped, and the session counts the loss so Review can say so.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, new FakeRecogniser { Confidence = 0.05, Words = [new OcrWord("blurry", 0, 0, 10, 10)] });

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        var session = (await store.LoadSessionAsync("s1"))!;
        Assert.Empty(session.Frames);
        Assert.Equal(1, session.FramesPurgedUnredacted);
        Assert.Equal(1, worker.Progress.Unreadable);
        Assert.Contains(await store.GetAuditAsync("s1"), a => a.Type == AuditTypes.FramesPurgedUnredacted);
    }

    [Fact]
    public async Task AFrameThatThrewIsDeletedRatherThanStored()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, new FakeRecogniser { Throw = true });

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        Assert.Empty((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.Equal(1, worker.Progress.Unreadable);
    }

    [Fact]
    public async Task AFrameTheMaskerCouldNotProcessIsDeletedRatherThanStored()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, Reads(new OcrWord("fine", 0, 0, 10, 10)), masker: new ThrowingMasker());

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        Assert.Empty((await store.LoadSessionAsync("s1"))!.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFrameWhoseScanCouldNotFinishIsDiscardedRatherThanStored(bool foundACardFirst)
    {
        // ADR-0004 on the frame path (INV-1): a pattern that ran out of its budget left text unsearched, so
        // the frame is gone and counted, never painted and stored as if it had been checked. Matches found
        // before the scan gave up do not rescue it: what was found is not everything that was there. The
        // stand-in reports late, after its last match, as the real library does when a detector times out.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var masker = new RecordingMasker();
        var engine = new RedactionEngine(
            RedactionPolicy.Default,
            (text, _, onIncomplete) => GiveUpAfter(text, foundACardFirst, onIncomplete));
        var worker = Worker(store, Reads(
            new OcrWord("Card", 10, 40, 50, 20),
            new OcrWord("4111111111111111", 70, 40, 200, 20)), masker, engine);

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        await AssertDiscardedUnsearchedAsync(store, worker, masker);
    }

    [Fact]
    public async Task ATenantPatternThatWillNotCompileDiscardsTheFrameRatherThanStoringItHalfSearched()
    {
        // The same rule through the real pattern library: an administrator's typo is a rule that never ran,
        // and the text it was meant to search is text nobody searched.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var masker = new RecordingMasker();
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["(unclosed"] });
        var worker = Worker(store, Reads(new OcrWord("Services", 0, 0, 80, 20)), masker, engine);

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        await AssertDiscardedUnsearchedAsync(store, worker, masker);
    }

    /// <summary>
    /// What a frame whose scan did not finish leaves behind: nothing painted, nothing stored, an audit row
    /// saying it was purged and none saying it was redacted, one Unreadable (not Unread, not Failed, not a
    /// frame that made it), and its queue slot given back.
    /// </summary>
    private async Task AssertDiscardedUnsearchedAsync(SqliteSessionStore store, RedactionWorker worker, RecordingMasker masker)
    {
        Assert.Empty(masker.Calls);
        var session = (await store.LoadSessionAsync("s1"))!;
        Assert.Empty(session.Frames);
        Assert.Null(await store.GetRedactedFrameImageAsync("f1"));
        Assert.Equal(1, session.FramesPurgedUnredacted);
        var audit = await store.GetAuditAsync("s1");
        Assert.Contains(audit, a => a.Type == AuditTypes.FramesPurgedUnredacted);
        Assert.DoesNotContain(audit, a => a.Type == AuditTypes.FrameRedacted);

        var progress = worker.Progress;
        Assert.Equal(1, progress.Unreadable);
        Assert.Equal(0, progress.Unread);
        Assert.Equal(0, progress.Frames);
        Assert.Equal(0, progress.Failed);
        Assert.Empty(progress.Masked);
        Assert.Equal(0, _pending.Depth);
    }

    [Fact]
    public async Task TheMaskerIsAskedToPaintEveryRegionThatWasFound()
    {
        // INV-1 is a claim about pixels. A worker that handed the masker an empty list, or only some of the
        // boxes, would store a card number in plain sight and record it as masked, and the text side of the
        // frame would still read "[CARD]". So the masker must get every box the engine placed, at the
        // configured edge, over the captured bytes; and what is stored is its output, with the same boxes.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var masker = new RecordingMasker();
        var worker = Worker(
            store,
            Reads(
                new OcrWord("Card", 10, 40, 50, 20),
                new OcrWord("4111111111111111", 70, 40, 200, 20),
                new OcrWord("123-45-6789", 70, 80, 120, 20)),
            masker,
            options: new RedactionOptions { MaxEdge = 1234 });

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        var call = Assert.Single(masker.Calls);
        Assert.Equal(1234, call.MaxEdge);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, call.Image);
        Assert.Collection(
            call.Regions.OrderBy(r => r.Y),
            card => Assert.Equal(new MaskedRegion { Kind = MaskKind.Card, X = 70, Y = 40, Width = 200, Height = 20 }, card),
            ssn => Assert.Equal(new MaskedRegion { Kind = MaskKind.Ssn, X = 70, Y = 80, Width = 120, Height = 20 }, ssn));

        var frame = Assert.Single((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.Equal(call.Regions, frame.MaskedRegions);
        Assert.Equal(new byte[] { 0xEE }, await store.GetRedactedFrameImageAsync("f1"));
        Assert.Equal(1, worker.Progress.Masked[MaskKind.Card]);
        Assert.Equal(1, worker.Progress.Masked[MaskKind.Ssn]);
    }

    [Fact]
    public async Task AFrameTheRecogniserReadNothingFromIsDeletedRatherThanStored()
    {
        // ST-048 (weaknesses P0-1), reversing the earlier reading that "no text" means "just quiet".
        // Nothing downstream can tell a wallpaper from a dark-mode terminal the engine could not segment,
        // a page in a language with no pack installed, or a frame it decoded and gave up on: all four
        // arrive here as zero words. Storing them as redacted marks a customer's screen clean without
        // anything having read it, which is the INV-1 breach this test exists to keep closed. ADR-0004
        // records the cost — genuinely blank frames are lost — and why it is the cheaper of the two.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, new FakeRecogniser { Words = [], Confidence = 1.0 });

        Assert.True(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        var session = (await store.LoadSessionAsync("s1"))!;
        Assert.Empty(session.Frames);
        Assert.Equal(1, session.FramesPurgedUnredacted);
    }

    [Fact]
    public async Task ReadingNothingIsCountedApartFromReadingBadly()
    {
        // Both end in a discarded frame, and they mean opposite things. A rising Unread is the recogniser
        // failing to see anything at all — a missing language pack, a theme it cannot handle — and a
        // rising Unreadable is it seeing badly. One number covering both would hide whichever is happening.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        await StageAsync(store, "s2", "f2");
        var readNothing = Worker(store, new FakeRecogniser { Words = [], Confidence = 1.0 });
        await readNothing.ProcessOneAsync(TestContext.Current.CancellationToken);

        var readBadly = Worker(store, new FakeRecogniser { Confidence = 0.05, Words = [new OcrWord("blurry", 0, 0, 10, 10)] });
        await readBadly.ProcessOneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, readNothing.Progress.Unread);
        Assert.Equal(0, readNothing.Progress.Unreadable);
        Assert.Equal(1, readBadly.Progress.Unreadable);
        Assert.Equal(0, readBadly.Progress.Unread);
    }

    [Fact]
    public async Task AFrameTheRecogniserSawNoWordsInIsNotCountedAsRedacted()
    {
        // The counter this bug hid behind: "read nothing" used to land in the success count, so a service
        // whose OCR engine had stopped reading anything at all reported a healthy frame rate.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, new FakeRecogniser { Words = [], Confidence = 1.0 });

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, worker.Progress.Frames);
    }

    [Fact]
    public async Task ALoginScreenIsMarkedAndSuppressesWhatComesNext()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, Reads(
            new OcrWord("Windows", 0, 0, 80, 20),
            new OcrWord("Security", 90, 0, 80, 20),
            new OcrWord("Password", 0, 40, 80, 20)));
        TimeSpan? suppressed = null;
        worker.SensitiveContextSeen += span => suppressed = span;

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        var frame = Assert.Single((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.True(frame.SensitiveContext);
        Assert.Equal(TimeSpan.FromSeconds(10), suppressed);
    }

    [Fact]
    public async Task FramesFromDifferentSessionsStayApart()
    {
        // Per-session isolation: two sessions redacting at once must not put one's frame on the other.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        await StageAsync(store, "s2", "f2");
        var worker = Worker(store, Reads(new OcrWord("hello", 0, 0, 40, 20)));

        while (await worker.ProcessOneAsync(TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal("f1", Assert.Single((await store.LoadSessionAsync("s1"))!.Frames).Id);
        Assert.Equal("f2", Assert.Single((await store.LoadSessionAsync("s2"))!.Frames).Id);
        Assert.Equal(2, worker.Progress.Frames);
    }

    [Fact]
    public async Task TheBacklogCountsEverySessionsWaitingFrames()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        await StageAsync(store, "s1", "f2");
        await StageAsync(store, "s2", "f3");

        // The backlog is what is waiting to be read, across every session. It was a query over the
        // frames table; it is the queue's own depth now, which is the same number for nothing
        // (ADR-0006).
        Assert.Equal(3, _pending.Depth);

        var worker = Worker(store, Reads(new OcrWord("hello", 0, 0, 40, 20)));
        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _pending.Depth);
    }

    [Fact]
    public async Task AnEmptyQueueIsNotAnError()
    {
        var store = await OpenAsync();
        var worker = Worker(store, Reads());

        Assert.False(await worker.ProcessOneAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhatWasMaskedIsCountedByKind()
    {
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store, Reads(
            new OcrWord("4111111111111111", 0, 0, 200, 20),
            new OcrWord("123-45-6789", 0, 40, 120, 20)));

        await worker.ProcessOneAsync(TestContext.Current.CancellationToken);

        var progress = worker.Progress;
        Assert.Equal(1, progress.Frames);
        Assert.Equal(1, progress.Masked[MaskKind.Card]);
        Assert.Equal(1, progress.Masked[MaskKind.Ssn]);
    }

    [Theory]
    [InlineData(true, "Windows Security Password")]
    [InlineData(true, "Sign in Username Password Remember me")]
    [InlineData(true, "Enter your passphrase to continue")]
    [InlineData(true, "Login Username Account Next")]
    // Spelled the way Windows and web forms actually draw it. OCR hands back the tokens on screen, so a
    // cue list that only knows "logon" and "sign in" misses the classic logon banner and every hyphenated
    // sign-in page — the screens this heuristic exists for.
    [InlineData(true, "Log On to Windows User name Domain")]
    [InlineData(true, "Sign-in Email Continue")]
    [InlineData(true, "Log-in Account Submit")]
    [InlineData(false, "Print Spooler Stopped Automatic")]
    [InlineData(false, "Inbox 4 new messages")]
    [InlineData(false, "Services Local Computer")]
    [InlineData(false, "")]
    public void TheLoginHeuristicKnowsASignInScreenFromAServicesList(bool expected, string words)
    {
        var recognised = words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => new OcrWord(w, i * 40, 0, 40, 20))
            .ToList();

        Assert.Equal(expected, LoginScreenHeuristic.LooksLikeLogin(recognised));
    }

    [Fact]
    public void AnArticleAboutPasswordPolicyIsStillASuspicion()
    {
        // Deliberately a false positive. "Password" on screen costs ten seconds of screenshots; missing a
        // real prompt costs a customer's credential in someone's ticket.
        Assert.True(LoginScreenHeuristic.LooksLikeLogin([new OcrWord("password", 0, 0, 80, 20), new OcrWord("policy", 90, 0, 60, 20)]));
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    [Fact]
    public async Task NothingUnredactedIsEverInTheStore()
    {
        // ADR-0006's own claim, checked at the only two moments it could be false: after the frame was
        // captured, and after it was written. There is no third moment — the bytes are held in memory in
        // between, and the store learns about the frame once, already masked.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");

        // Captured and waiting: the store has never heard of it.
        Assert.Equal(0, await store.CountAllPendingFramesAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await store.LoadSessionAsync("s1"))!.Frames);

        Assert.True(await Worker(store, Reads(new OcrWord("Services", 0, 0, 40, 20)))
            .ProcessOneAsync(TestContext.Current.CancellationToken));

        // Written, and what was written is the masker's output rather than what was captured. The fake
        // masker appends a byte, so the two are told apart by their length.
        Assert.Equal(0, await store.CountAllPendingFramesAsync(TestContext.Current.CancellationToken));
        var frame = Assert.Single((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.False(frame.RedactionPending);
        Assert.Equal(5, (await store.GetRedactedFrameImageAsync("f1"))!.Length);
    }

    private async Task<SqliteSessionStore> OpenAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

    /// <summary>
    /// A captured frame waiting to be read.
    ///
    /// Deep on purpose: what these tests are about is what the worker does with a frame, and the
    /// production depth of four would make a test that captures five fail for a reason it is not
    /// asking about. PendingFramesTests owns the depth (ADR-0006).
    /// </summary>
    private readonly PendingFrames _pending = new(depth: 1000);

    private async Task StageAsync(SqliteSessionStore store, string sessionId, string frameId)
    {
        if (await store.LoadSessionAsync(sessionId) is null)
        {
            await store.CreateSessionAsync(new NewSession(sessionId, At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        }

        // Into the queue, not the store: an unredacted frame does not reach disk (ADR-0006).
        Assert.True(_pending.TryEnqueue(sessionId, new StagedFrame(frameId, 1_000, FrameTrigger.Click, 800, 600, null, new byte[] { 1, 2, 3, 4 })));
    }

    private RedactionWorker Worker(
        SqliteSessionStore store,
        IFrameTextRecogniser recogniser,
        IFrameMasker? masker = null,
        RedactionEngine? engine = null,
        RedactionOptions? options = null) =>
        new(store, recogniser, masker ?? new FakeMasker(), engine ?? new RedactionEngine(), _pending, options);

    /// <summary>
    /// A pattern library that runs out of budget: it may report a card first, and then says the scan did
    /// not finish. Lazily, after the last match, which is when the real library learns a detector timed out.
    /// </summary>
    private static IEnumerable<PatternMatch> GiveUpAfter(string text, bool findCard, Action onIncomplete)
    {
        if (findCard)
        {
            yield return new PatternMatch(MaskKind.Card, text.IndexOf("4111", StringComparison.Ordinal), 16, "[CARD]");
        }

        onIncomplete();
    }

    private static FakeRecogniser Reads(params OcrWord[] words) => new() { Words = words, Confidence = 0.95 };

    private sealed class FakeRecogniser : IFrameTextRecogniser
    {
        public IReadOnlyList<OcrWord> Words { get; init; } = [];

        public double Confidence { get; init; } = 0.95;

        public bool Throw { get; init; }

        public Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default) =>
            Throw
                ? throw new InvalidOperationException("the recogniser fell over")
                : Task.FromResult(new RecognisedText(Words, Confidence));
    }

    /// <summary>Records what it was asked to cover, without needing a real image decoder on macOS.</summary>
    private sealed class FakeMasker : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            new([.. image.Span.ToArray(), (byte)regions.Count], 800, 600);
    }

    /// <summary>Keeps what it was asked to paint and returns a marker byte, so the stored image is known to be its output.</summary>
    private sealed class RecordingMasker : IFrameMasker
    {
        public List<(byte[] Image, IReadOnlyList<MaskedRegion> Regions, int MaxEdge)> Calls { get; } = [];

        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge)
        {
            Calls.Add((image.ToArray(), [.. regions], maxEdge));
            return new MaskedImage([0xEE], 800, 600);
        }
    }

    private sealed class ThrowingMasker : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            throw new InvalidOperationException("not an image this decoder understands");
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
