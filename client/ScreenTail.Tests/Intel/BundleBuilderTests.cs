using System.Text;
using ScreenTail.Core.Intel;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Intel;

/// <summary>
/// ST-060. The bundle is the one thing that leaves the machine (INV-7, INV-8), so what it may not
/// contain matters more than what it does.
///
/// Every exclusion below is written as a regression test on purpose: remove the filter and the test
/// fails. The review that produced ST-048 found the opposite shape throughout — well-written decision
/// classes with no test between them and the data — and this is the layer where that mistake would send
/// a customer's unredacted screen to a model provider.
/// </summary>
public sealed class BundleBuilderTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APendingFrameIsNeverIncluded()
    {
        // INV-1, at the last place it can still be broken. A frame nobody has read is exactly what the
        // redaction worker exists to stop reaching a reader, and a model provider is a reader.
        var bundle = BundleBuilder.Build(Session(
            Frame("f1", 1_000),
            Frame("f2", 2_000) with { RedactionPending = true, RedactedAt = null, OcrText = null }));

        Assert.Equal(["f1"], bundle.Frames.Select(f => f.Id));
    }

    [Fact]
    public void ASensitiveContextFrameIsNeverIncluded()
    {
        // The login heuristic marked it: the frame looked like a sign-in screen. Redaction masked what it
        // found, and what it found is not the same as what was there.
        var bundle = BundleBuilder.Build(Session(
            Frame("f1", 1_000),
            Frame("f2", 2_000) with { SensitiveContext = true }));

        Assert.Equal(["f1"], bundle.Frames.Select(f => f.Id));
    }

    [Fact]
    public void AFrameFromASuppressedStretchIsNeverIncluded()
    {
        // INV-6 says suppression drops data rather than hiding it, so a frame timestamped inside a
        // suppressed interval should not exist at all. This is the belt to that braces: if one ever does,
        // it is the single most dangerous frame in the session, because something was suppressing capture
        // when it was taken.
        var session = Session(Frame("f1", 1_000), Frame("f2", 5_000), Frame("f3", 9_000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 4_000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 6_000, State = CaptureState.Recording },
            ],
        };

        var bundle = BundleBuilder.Build(session);

        Assert.Equal(["f1", "f3"], bundle.Frames.Select(f => f.Id));
    }

    [Fact]
    public void AFrameFromAPausedStretchIsNeverIncluded()
    {
        var session = Session(Frame("f1", 1_000), Frame("f2", 5_000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 4_000, State = CaptureState.Paused },
                new CaptureStateEvent { TsMs = 8_000, State = CaptureState.Recording },
            ],
        };

        Assert.Equal(["f1"], BundleBuilder.Build(session).Frames.Select(f => f.Id));
    }

    [Fact]
    public void ASuppressionThatNeverEndsStillExcludesEverythingAfterIt()
    {
        // A session that ended while suppressed. Leaving the interval open is the safe reading: the
        // alternative treats "we never heard it resume" as "it resumed".
        var session = Session(Frame("f1", 1_000), Frame("f2", 5_000), Frame("f3", 9_000)) with
        {
            Events = [new CaptureStateEvent { TsMs = 4_000, State = CaptureState.Suppressed }],
        };

        Assert.Equal(["f1"], BundleBuilder.Build(session).Frames.Select(f => f.Id));
    }

    [Fact]
    public void AFrameTheTechnicianRemovedIsNeverIncluded()
    {
        // Review's "remove this screenshot" (ST-075). The technician looked at it and said no, which is
        // the most explicit instruction anything in this system ever receives.
        var bundle = BundleBuilder.Build(Session(
            Frame("f1", 1_000),
            Frame("f2", 2_000) with { ExcludedByUser = true }));

        Assert.Equal(["f1"], bundle.Frames.Select(f => f.Id));
    }

    [Fact]
    public void TwoHundredFramesBecomeAtMostTwentyFive()
    {
        var session = Session([.. Enumerable.Range(0, 200).Select(i => Frame($"f{i:D3}", i * 1_000))]);

        var bundle = BundleBuilder.Build(session);

        Assert.True(bundle.Frames.Count <= 25, $"{bundle.Frames.Count} frames is more than the ticket's ceiling of 25");
        Assert.Equal(200, bundle.FramesConsidered);
    }

    [Fact]
    public void ThePayloadStaysUnderFourMegabytes()
    {
        // Twenty-five frames is a count, not a size. Big screenshots hit the byte budget first, and a
        // request that is refused for being too large costs the whole note rather than a few pictures.
        var heavy = new string('A', 300_000);
        var session = Session([.. Enumerable.Range(0, 200).Select(i => Frame($"f{i:D3}", i * 1_000) with { Image = heavy })]);

        var bundle = BundleBuilder.Build(session);

        Assert.True(bundle.EstimatedBytes < 4 * 1024 * 1024, $"{bundle.EstimatedBytes} bytes is over the 4 MB budget");
        Assert.True(bundle.Frames.Count < 25, "the byte budget should have bitten before the count did");
    }

    [Fact]
    public void TheSelectionCoversTheWholeSessionNotJustTheStart()
    {
        // A note built from the first twenty-five clicks describes opening a console and nothing else.
        // What the technician did at the end is usually the fix.
        var session = Session([.. Enumerable.Range(0, 200).Select(i => Frame($"f{i:D3}", i * 1_000))]);

        var bundle = BundleBuilder.Build(session);

        var last = bundle.Frames[^1].TsMs;
        Assert.True(last > 150_000, $"the last frame chosen was at {last} ms of a 199 s session");
        Assert.Equal(0, bundle.Frames[0].TsMs);
    }

    [Fact]
    public void AFrameNobodyCouldReadLosesToOneWithWordsOnIt()
    {
        // A screenshot with no text tells a drafting model almost nothing. Given a choice, spend the
        // budget on the one that says something.
        var session = Session(
            Frame("blank", 1_000) with { OcrText = null },
            Frame("words", 2_000) with { OcrText = "The print spooler service is not running" });

        var bundle = BundleBuilder.Build(session, new BundleOptions { MaxFrames = 1 });

        Assert.Equal("words", Assert.Single(bundle.Frames).Id);
    }

    [Fact]
    public void AFrameTheTechnicianTalkedOverIsPreferred()
    {
        // The strongest signal there is that a screenshot mattered: they explained it out loud.
        var session = Session(
            Frame("quiet", 1_000) with { OcrText = "Services" },
            Frame("explained", 60_000) with { OcrText = "Services" }) with
        {
            Transcript =
            [
                new TranscriptSegment
                {
                    Id = "t1",
                    TsMs = 59_000,
                    EndMs = 62_000,
                    Speaker = Speaker.Tech,
                    Text = "so the spooler was stopped and I started it",
                    Confidence = 0.9,
                },
            ],
        };

        var bundle = BundleBuilder.Build(session, new BundleOptions { MaxFrames = 1 });

        Assert.Equal("explained", Assert.Single(bundle.Frames).Id);
    }

    [Fact]
    public void WhatWasSaidComesBackAttachedToTheFrameItWasAbout()
    {
        var session = Session(Frame("f1", 12_000)) with
        {
            Transcript =
            [
                new TranscriptSegment { Id = "t1", TsMs = 10_500, EndMs = 14_200, Speaker = Speaker.Tech, Text = "restarting it now", Confidence = 0.9 },
                new TranscriptSegment { Id = "t2", TsMs = 90_000, EndMs = 92_000, Speaker = Speaker.Tech, Text = "unrelated", Confidence = 0.9 },
            ],
        };

        var bundle = BundleBuilder.Build(session);

        Assert.Equal("f1", bundle.Transcript.Single(s => s.Id == "t1").FrameId);
        Assert.Null(bundle.Transcript.Single(s => s.Id == "t2").FrameId);
    }

    [Fact]
    public void SpeechAboutAFrameThatWasDroppedIsNotLeftPointingAtIt()
    {
        // A dangling reference is worse than none: the note prompt rejects them, so one sentence would
        // cost the whole draft. The frame went because it was sensitive; what was said about it stays,
        // as narration.
        var session = Session(Frame("gone", 12_000) with { SensitiveContext = true }) with
        {
            Transcript =
            [
                new TranscriptSegment { Id = "t1", TsMs = 11_000, EndMs = 13_000, Speaker = Speaker.Tech, Text = "typing the password", Confidence = 0.9 },
            ],
        };

        var bundle = BundleBuilder.Build(session);

        Assert.Empty(bundle.Frames);
        Assert.Null(Assert.Single(bundle.Transcript).FrameId);
    }

    [Fact]
    public void OcrPartialSaysSoWhenSomethingCouldNotBeRead()
    {
        // The drafting prompt is allowed to hedge when it knows it is working from an incomplete picture,
        // and cannot when nothing tells it.
        var complete = Session(Frame("f1", 1_000) with { OcrText = "Services" });
        var partial = Session(
            Frame("f1", 1_000) with { OcrText = "Services" },
            Frame("f2", 2_000) with { OcrText = null });

        Assert.False(BundleBuilder.Build(complete).OcrPartial);
        Assert.True(BundleBuilder.Build(partial).OcrPartial);
    }

    [Fact]
    public void WhatWasLostToRedactionIsCarriedThrough()
    {
        var session = Session(Frame("f1", 1_000)) with { FramesPurgedUnredacted = 3, PartialCapture = true };

        var bundle = BundleBuilder.Build(session);

        Assert.Equal(3, bundle.FramesPurgedUnredacted);
        Assert.True(bundle.PartialCapture);
    }

    [Fact]
    public void TheTokenEstimateCountsBothWordsAndPictures()
    {
        var text = Session(Frame("f1", 1_000) with { OcrText = new string('x', 4_000) });
        var none = Session(Frame("f1", 1_000) with { OcrText = null });

        Assert.True(BundleBuilder.Build(text).EstimatedTokens > BundleBuilder.Build(none).EstimatedTokens);
        Assert.True(BundleBuilder.Build(none).EstimatedTokens > 0, "a picture costs tokens even with no text on it");
    }

    [Fact]
    public void TheSameSessionBuildsTheSameBundleTwice()
    {
        // A bundle that varies between runs makes a draft that varies between runs, and a technician
        // cannot review a note that is different every time they look.
        var session = Session([.. Enumerable.Range(0, 60).Select(i => Frame($"f{i:D3}", i * 1_000))]);

        Assert.Equal(
            BundleBuilder.Build(session).Frames.Select(f => f.Id),
            BundleBuilder.Build(session).Frames.Select(f => f.Id));
    }

    [Fact]
    public void FramesComeBackInTheOrderTheyHappened()
    {
        var session = Session([.. Enumerable.Range(0, 60).Select(i => Frame($"f{i:D3}", i * 1_000))]);

        var times = BundleBuilder.Build(session).Frames.Select(f => f.TsMs).ToList();

        Assert.Equal(times.OrderBy(t => t), times);
    }

    [Fact]
    public void ASessionWithNothingUsableStillBuildsABundle()
    {
        // Every frame pending. The note is worse and the request still goes: the transcript and the click
        // timeline are often enough for Problem and Result, and returning nothing would lose those too.
        var session = Session(Frame("f1", 1_000) with { RedactionPending = true, RedactedAt = null });

        var bundle = BundleBuilder.Build(session);

        Assert.Empty(bundle.Frames);
        Assert.Equal("s1", bundle.SessionId);
    }

    [Fact]
    public void NothingInTheBundleCarriesAWindowTitle()
    {
        // INV-10. The bundle is built from a session that never held a title, and this is the test that
        // notices the day someone adds one to make the drafting better.
        var json = System.Text.Json.JsonSerializer.Serialize(
            BundleBuilder.Build(Session(Frame("f1", 1_000))),
            SessionJson.Options);

        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
    }

    private static Session Session(params Frame[] frames) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s1",
        StartedAt = At,
        DurationMs = 200_000,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Frames = frames,
        Transcript = [],
    };

    private static Frame Frame(string id, long tsMs) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = Convert.ToBase64String(Encoding.UTF8.GetBytes($"image-{id}")),
        Width = 1920,
        Height = 1080,
        RedactionPending = false,
        RedactedAt = At,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
        OcrText = "Services Local Computer",
    };
}
