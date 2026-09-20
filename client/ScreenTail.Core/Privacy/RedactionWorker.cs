using System.Diagnostics;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

public sealed record RedactionOptions
{
    /// <summary>
    /// How many frames are processed at once. OCR is the expensive part and it is CPU-bound, so this is
    /// deliberately small: ST-031 budgets 15% of the machine for all of ScreenTail, and a technician's
    /// remote session must stay responsive while their own machine reads screenshots in the background.
    /// </summary>
    public int Concurrency { get; init; } = 2;

    /// <summary>Long edge of the stored frame, after masking (ADR-0001 finding 2a).</summary>
    public int MaxEdge { get; init; } = Downscale.MaxEdge;

    /// <summary>How often the backlog depth is reported to the HUD (Spec §5 S1).</summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Below this, a frame is treated as unreadable rather than as clean. A page of text the recogniser
    /// could not make out is the one place a secret is most likely to be hiding.
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.3;
}

/// <param name="Frames">How many frames have been redacted since the worker started.</param>
/// <param name="Unread">
/// Frames the recogniser returned no words at all for, which are discarded rather than stored (ST-048,
/// ADR-0004). Counted apart from <paramref name="Unreadable"/> because the two mean opposite things: this
/// is the engine seeing nothing, that is the engine seeing badly. A number climbing here on a machine that
/// is plainly showing text is how a missing language pack or an unsupported theme announces itself.
/// </param>
/// <param name="Masked">How many regions were painted over, by kind. Counts only (INV-10).</param>
/// <param name="Spent">Time spent redacting, across all frames — the numerator of the per-frame cost.</param>
/// <param name="Slowest">The worst single frame, which is what a technician notices, not the average.</param>
/// <param name="Failed">
/// Frames whose redaction threw. Counted rather than swallowed: a rising number here means redaction is
/// broken, and the alternative was a worker that died and took every future screenshot with it.
/// </param>
public sealed record RedactionProgress(
    long Frames,
    long Unreadable,
    long Unread,
    IReadOnlyDictionary<MaskKind, long> Masked,
    TimeSpan Spent = default,
    TimeSpan Slowest = default,
    long Failed = 0)
{
    /// <summary>ST-041 budgets 700 ms a frame; this is the number that gets compared to it.</summary>
    public TimeSpan PerFrame => Frames == 0 ? TimeSpan.Zero : Spent / Frames;
}

/// <summary>
/// Turns staged frames into readable ones (ST-041).
///
/// This is the only thing in the system allowed to read a pending frame, and the only thing that can clear
/// <c>redaction_pending</c>. Everything downstream — Review, the bundle builder, export — reads through an
/// API that returns redacted frames only, so INV-1 reduces to a single claim about this class: a frame it
/// marks readable has had its secrets painted over first.
///
/// The order matters and is not negotiable. Read the text at native resolution, decide what to cover,
/// paint over it, and only then shrink for storage. Shrinking first would move every box slightly and
/// leave slivers of what was meant to be covered.
///
/// A frame it cannot process is deleted rather than stored. There is no third option: an unreadable frame
/// kept "just in case" is an unredacted frame, and the session is better off missing a screenshot than
/// carrying one nobody checked.
/// </summary>
public sealed class RedactionWorker(
    ISessionStore store,
    IFrameTextRecogniser recogniser,
    IFrameMasker masker,
    RedactionEngine engine,
    RedactionOptions? options = null,
    TimeProvider? time = null)
{
    private readonly RedactionOptions _options = options ?? new RedactionOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<MaskKind, long> _masked = [];
    private readonly Lock _counters = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    /// <summary>
    /// Frames that could not even be discarded, so the decision could not be written down anywhere.
    ///
    /// A queue rather than a set, and bounded by <see cref="MaxRemembered"/>: it is skipped on every
    /// poll, and a list that grows for the life of the service is both a leak and a query that gets
    /// slower all day.
    /// </summary>
    private readonly Queue<string> _givenUp = new();

    /// <summary>
    /// How many un-discardable frames are skipped at once. Enough to get past a handful that are
    /// genuinely stuck, small enough that the NOT IN list stays short.
    /// </summary>
    private const int MaxRemembered = 16;
    private long _frames;
    private long _unreadable;
    private long _unread;
    private long _failed;
    private TimeSpan _spent;
    private TimeSpan _slowest;

    /// <summary>Reports how many frames are still waiting, for the HUD and the diagnostics panel.</summary>
    public event Action<int>? BacklogChanged;

    /// <summary>How long a frame that looked like a login screen suppresses capture for.</summary>
    public event Action<TimeSpan>? SensitiveContextSeen;

    public RedactionProgress Progress
    {
        get
        {
            lock (_counters)
            {
                return new RedactionProgress(
                    _frames, _unreadable, _unread, new Dictionary<MaskKind, long>(_masked), _spent, _slowest, _failed);
            }
        }
    }

    /// <summary>Drains the queue until cancelled. One call; concurrency is handled inside.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var reporting = ReportBacklogAsync(ct);
        var workers = Enumerable.Range(0, _options.Concurrency).Select(_ => WorkAsync(ct)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        await reporting.ConfigureAwait(false);
    }

    /// <summary>Processes one frame if any is waiting. Returns false when the queue is empty.</summary>
    public async Task<bool> ProcessOneAsync(CancellationToken ct = default)
    {
        HashSet<string> busy;
        lock (_counters)
        {
            busy = [.. _inFlight, .. _givenUp];
        }

        var frame = await store.TakeNextPendingFrameAsync(busy, ct).ConfigureAwait(false);
        if (frame is null)
        {
            return false;
        }

        // Claimed before any await that could let the other worker in. A frame stays redaction_pending
        // until its redaction finishes, so the query cannot tell "waiting" from "being worked on": without
        // this both workers take the same row, both run OCR on it, and the slower one's write finds
        // nothing to update and throws.
        lock (_counters)
        {
            if (!_inFlight.Add(frame.Id))
            {
                return false;
            }
        }

        try
        {
            await RedactAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Given up on rather than retried. A frame whose redaction throws will throw again — the
            // store refusing a write does not become a different store on the next pass — and retrying
            // it is a tight loop burning the CPU budget on one frame forever.
            //
            // Discarded rather than remembered. Nobody has read it, so nobody can say what is on it, and
            // ADR-0004 says such a frame goes; leaving it pending for finalize was the same answer
            // reached later. Remembering it instead cost three things, all of which showed up in review:
            // a set that grew for the life of the service, a NOT IN list on every poll that grew with
            // it, and a backlog count that never reached zero again — so the pill showed a technician
            // work that was never going to finish (weaknesses P1-9).
            lock (_counters)
            {
                _failed++;
            }

            await ForgetAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_counters)
            {
                _inFlight.Remove(frame.Id);
            }
        }

        return true;
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await StepAsync(ct).ConfigureAwait(false))
                {
                    // Nothing waiting. Frames arrive on clicks, so idling briefly costs nothing and keeps
                    // the worker off the CPU while the technician is reading rather than clicking.
                    await Task.Delay(TimeSpan.FromMilliseconds(200), _time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The loop's last line of defence.
    ///
    /// Before this, an exception from anywhere outside <see cref="RedactAsync"/>'s own try blocks — most
    /// realistically the store refusing a write for a frame something else had already redacted — faulted
    /// the worker task and ended the loop. Both workers went the same way, and from then on every staged
    /// frame stayed pending until finalize purged it: a service that produced no screenshots at all for
    /// the rest of the day, with nothing in the log to say why.
    ///
    /// <see cref="ProcessOneAsync"/> already handles a frame that fails; this catches everything else, so
    /// that no future change can end the loop by throwing somewhere new.
    /// </summary>
    private async Task<bool> StepAsync(CancellationToken ct)
    {
        try
        {
            return await ProcessOneAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_counters)
            {
                _failed++;
            }

            // False, so the loop waits before trying again rather than spinning on whatever is wrong.
            return false;
        }
    }

    private async Task RedactAsync(PendingFrame frame, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        RecognisedText text;
        try
        {
            text = await recogniser.ReadAsync(frame.Image, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The recogniser failed, so nothing about this frame is known. It cannot be stored.
            await DiscardAsync(frame, unread: false, ct).ConfigureAwait(false);
            return;
        }

        // Zero words is not a clean frame, it is an unread one. Windows.Media.Ocr returns nothing at all
        // for a page in a language with no pack installed, a theme it cannot segment, and a blank desktop
        // alike, so nothing downstream can tell the harmless case from the dangerous one (ADR-0001
        // finding 9). Storing it would clear redaction_pending on a frame no reader has ever seen, which
        // is the one thing INV-1 forbids. ADR-0004 records the trade: genuinely blank frames are lost, and
        // a screenshot of a wallpaper is worth less than the chance that the frame was a terminal.
        if (text.IsEmpty)
        {
            await DiscardAsync(frame, unread: true, ct).ConfigureAwait(false);
            return;
        }

        // Too little confidence is not the same as nothing to hide. A frame the recogniser could not make
        // out is where a secret is most likely to survive, so it goes rather than being stored unchecked.
        if (text.MeanConfidence < _options.MinimumConfidence)
        {
            await DiscardAsync(frame, unread: false, ct).ConfigureAwait(false);
            return;
        }

        var redaction = engine.RedactFrame(text.Words);
        if (!redaction.Complete)
        {
            // A pattern ran out of its budget, so parts of this text were never searched (ST-042).
            await DiscardAsync(frame, unread: false, ct).ConfigureAwait(false);
            return;
        }

        var regions = redaction.Regions.ToList();
        var sensitive = LoginScreenHeuristic.LooksLikeLogin(text.Words);
        if (sensitive)
        {
            SensitiveContextSeen?.Invoke(LoginScreenHeuristic.Suppression);
        }

        MaskedImage masked;
        try
        {
            // Masking happens at the size the regions were found in; the downscale comes after.
            masked = masker.Mask(frame.Image, regions, _options.MaxEdge);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DiscardAsync(frame, unread: false, ct).ConfigureAwait(false);
            return;
        }

        await store.MarkFrameRedactedAsync(
            frame.Id,
            new RedactionOutcome(masked.Image, redaction.Text, regions, sensitive, _time.GetUtcNow()),
            ct).ConfigureAwait(false);

        // ST-045: what was covered over, counted by kind. The customer's question is "what did it find on
        // my screen", and the honest answer is a number per kind — never the matches themselves (INV-10).
        if (store is IAuditLog audit)
        {
            foreach (var (kind, count) in redaction.Counts)
            {
                await audit.RecordAsync(
                    AuditTypes.FrameRedacted, frame.SessionId, count, AuditDetail.Of(kind), ct).ConfigureAwait(false);
            }
        }

        Record(redaction.Counts, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Deletes a frame that could not be redacted. The session keeps its count of what was lost, so Review
    /// can say "3 screenshots were removed because they could not be redacted in time" rather than leaving
    /// a silent gap (Spec §5 S3).
    /// </summary>
    /// <param name="unread">
    /// True when the recogniser returned no words, false when it read something unusable. Both frames go;
    /// they are counted apart so that an engine which has stopped reading anything is visible as itself
    /// rather than as a slow rise in unreadable frames.
    /// </param>
    /// <summary>
    /// Removes a frame that could not be processed, or — when even that fails — remembers it briefly.
    ///
    /// A full disk refuses the discard as well, and then there is nowhere to record the decision, so it
    /// has to be held in memory. Bounded, and oldest-out: a frame pushed out of the set is tried again,
    /// which is the right way round. A temporary failure recovers on its own, and a permanent one costs
    /// a bounded amount of repeated work instead of an unbounded amount of memory.
    /// </summary>
    private async Task ForgetAsync(PendingFrame frame, CancellationToken ct)
    {
        try
        {
            await store.DiscardPendingFrameAsync(frame.Id, ct).ConfigureAwait(false);
            lock (_counters)
            {
                _unreadable++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_counters)
            {
                _givenUp.Enqueue(frame.Id);
                while (_givenUp.Count > MaxRemembered)
                {
                    _ = _givenUp.Dequeue();
                }
            }
        }
    }

    private async Task DiscardAsync(PendingFrame frame, bool unread, CancellationToken ct)
    {
        await store.DiscardPendingFrameAsync(frame.Id, ct).ConfigureAwait(false);
        lock (_counters)
        {
            if (unread)
            {
                _unread++;
            }
            else
            {
                _unreadable++;
            }
        }
    }

    private void Record(IReadOnlyDictionary<MaskKind, int> counts, TimeSpan took)
    {
        lock (_counters)
        {
            _frames++;
            _spent += took;
            if (took > _slowest)
            {
                _slowest = took;
            }

            foreach (var (kind, count) in counts)
            {
                _masked[kind] = _masked.GetValueOrDefault(kind) + count;
            }
        }
    }

    private async Task ReportBacklogAsync(CancellationToken ct)
    {
        if (BacklogChanged is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.ReportEvery, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                BacklogChanged?.Invoke(await store.CountAllPendingFramesAsync(ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
