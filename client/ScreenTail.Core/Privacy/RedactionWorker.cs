using System.Diagnostics;
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
/// <param name="Masked">How many regions were painted over, by kind. Counts only (INV-10).</param>
/// <param name="Spent">Time spent redacting, across all frames — the numerator of the per-frame cost.</param>
/// <param name="Slowest">The worst single frame, which is what a technician notices, not the average.</param>
public sealed record RedactionProgress(
    long Frames,
    long Unreadable,
    IReadOnlyDictionary<MaskKind, long> Masked,
    TimeSpan Spent = default,
    TimeSpan Slowest = default)
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
    private long _frames;
    private long _unreadable;
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
                    _frames, _unreadable, new Dictionary<MaskKind, long>(_masked), _spent, _slowest);
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
        var frame = await store.TakeNextPendingFrameAsync(ct).ConfigureAwait(false);
        if (frame is null)
        {
            return false;
        }

        await RedactAsync(frame, ct).ConfigureAwait(false);
        return true;
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ProcessOneAsync(ct).ConfigureAwait(false))
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
            await DiscardAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        // Too little confidence is not the same as nothing to hide. A frame the recogniser could not make
        // out is where a secret is most likely to survive, so it goes rather than being stored unchecked.
        if (!text.IsEmpty && text.MeanConfidence < _options.MinimumConfidence)
        {
            await DiscardAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        var redaction = engine.RedactFrame(text.Words);
        if (!redaction.Complete)
        {
            // A pattern ran out of its budget, so parts of this text were never searched (ST-042).
            await DiscardAsync(frame, ct).ConfigureAwait(false);
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
            await DiscardAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        await store.MarkFrameRedactedAsync(
            frame.Id,
            new RedactionOutcome(masked.Image, redaction.Text, regions, sensitive, _time.GetUtcNow()),
            ct).ConfigureAwait(false);

        Record(redaction.Counts, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Deletes a frame that could not be redacted. The session keeps its count of what was lost, so Review
    /// can say "3 screenshots were removed because they could not be redacted in time" rather than leaving
    /// a silent gap (Spec §5 S3).
    /// </summary>
    private async Task DiscardAsync(PendingFrame frame, CancellationToken ct)
    {
        await store.DiscardPendingFrameAsync(frame.Id, ct).ConfigureAwait(false);
        lock (_counters)
        {
            _unreadable++;
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
