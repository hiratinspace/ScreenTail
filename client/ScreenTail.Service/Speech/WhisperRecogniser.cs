using System.Runtime.Versioning;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using Whisper.net;

namespace ScreenTail.Service.Speech;

/// <summary>
/// Local transcription with whisper.cpp (ST-027).
///
/// <b>Local, and not as a configuration choice.</b> A transcript of a support call is the most sensitive
/// thing a session produces (it is the technician saying out loud what they are doing to a customer's
/// machine), so it never leaves the device whatever the tenant's egress settings say. The only thing that
/// crosses the network here is the model file, which is public, hash-checked and not customer data.
///
/// The model is fetched on first use and can be hundreds of megabytes. <see cref="Ready"/> stays false
/// until it has landed and the factory has loaded it; speech heard before then is dropped and counted by
/// the recorder rather than queued, because a queue that grew for a whole session would then try to
/// transcribe an hour of audio at once (AC3).
///
/// <b>And it is let go of again.</b> Only the lazy-load half of that was built in 2026-09-20: once the
/// first session had loaded the model it stayed resident until the service stopped, 150 to 300 MB for
/// base.en and more for small.en, against ST-031's 600 MB for the whole of ScreenTail, on a machine that
/// may have recorded one call that morning (2026-10-12 review). After <see cref="UnloadAfter"/> with
/// nothing to transcribe the context and the model are released. <see cref="Ready"/> stays true: the model
/// is still on disk and verified, and the next segment reloads it under the same lock, which costs that
/// segment about a second on a machine that has the file cached and costs the host nothing to arrange.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WhisperRecogniser : ISpeechRecogniser
{
    /// <summary>
    /// How many threads whisper.cpp may use.
    ///
    /// Unset, it takes every hardware thread the machine has: eight on the reference laptop, which is
    /// the whole processor. ADR-0001 measured base.en that way at 49-62% of the CPU, against ST-031's
    /// 15% for all of ScreenTail, on a machine whose real job is the remote-desktop session the
    /// technician is running. Two threads is roughly a quarter of that, which lands near the budget
    /// once voice-activity detection has already cut the work down to the parts where somebody is
    /// actually speaking (2026-09-20 review).
    ///
    /// Not zero-cost: a segment takes longer to transcribe. It is transcribed after the fact either way,
    /// and a technician waiting a moment longer for a note is better than a technician whose screen
    /// share stutters.
    /// </summary>
    private const int Threads = 2;

    /// <summary>
    /// How long the model stays loaded with nothing to transcribe before it is released.
    ///
    /// Longer than the quiet stretches inside a session, where a reload would cost the first segment
    /// after them a second or so on a machine that is already busy, and short enough that a service which
    /// recorded one call in the morning is not carrying the model at lunch. The resident figure it saves
    /// needs the laptop to measure: ADR-0001's 505 MB is the whole process, not the model.
    /// </summary>
    public static TimeSpan DefaultUnloadAfter { get; } = TimeSpan.FromMinutes(5);

    private readonly SpeechModel _model;
    private readonly string _path;
    private readonly ModelDownload _download;
    private readonly IdleTimer _idle;

    /// <summary>
    /// One at a time. whisper.cpp holds state per processor, and two overlapping calls on one processor
    /// interleave into a transcript of neither. Loading and unloading happen under it too, so a release
    /// never takes the processor out from under a segment.
    /// </summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>
    /// Cancelled when the service stops, so a transcription in flight is told to give up. Whisper.net 1.9.1
    /// hands the token given to <c>ProcessAsync</c> to whisper.cpp as its abort and encoder-begin callbacks
    /// (<c>OnWhisperAbortStatic</c> reads <c>IsCancellationRequested</c>), so the native call stops at its
    /// next check rather than finishing the segment (ST-112).
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _stopWait;
    private readonly TimeProvider _time;
    private readonly Action? _abandoned;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private volatile bool _loadable;

    /// <summary>
    /// 0 until the first <see cref="DisposeAsync"/>, then 1, set with <see cref="Interlocked.Exchange(ref int, int)"/>
    /// so exactly one caller runs the release however many arrive at once.
    /// </summary>
    private int _disposing;

    /// <summary>Done when the first disposal has finished (or stopped waiting at its bound); later callers wait for it.</summary>
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool Disposed => Volatile.Read(ref _disposing) != 0;

    /// <param name="unloadAfter">How long with nothing to transcribe before the model is released. <see cref="DefaultUnloadAfter"/> when null.</param>
    /// <param name="time">The clock the idle period and the stop's wait are measured on. The system's when null.</param>
    /// <param name="stopWait">How long disposal waits for the model to let go. <see cref="StopBound.Default"/> when null.</param>
    /// <param name="abandoned">Told, once, when disposal stopped waiting for a native call still running. Content-free.</param>
    public WhisperRecogniser(
        ModelDownload download,
        SpeechModel? model = null,
        string? directory = null,
        TimeSpan? unloadAfter = null,
        TimeProvider? time = null,
        TimeSpan? stopWait = null,
        Action? abandoned = null)
    {
        _time = time ?? TimeProvider.System;
        _stopWait = stopWait ?? StopBound.Default;
        _abandoned = abandoned;
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _model = model ?? SpeechModels.For(Environment.ProcessorCount);
        _path = Path.Combine(
            directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTail",
                "models"),
            $"ggml-{_model.Name}.bin");
        UnloadAfter = unloadAfter ?? DefaultUnloadAfter;
        _idle = new IdleTimer(UnloadAfter, uses => _ = UnloadIfIdleAsync(uses), time);
    }

    /// <summary>
    /// The model is on disk, verified, and has loaded at least once. True across an idle release: the
    /// recorder may keep queueing, and the next segment brings the model back.
    /// </summary>
    public bool Ready => _loadable;

    /// <summary>
    /// For a test only: says the model loaded without loading one, so a test on a machine with no model
    /// file can reach the lock in <see cref="TranscribeAsync"/>. The service never sets it.
    /// </summary>
    internal bool LoadableForTests
    {
        init => _loadable = value;
    }

    /// <summary>
    /// For a test only: runs in <see cref="TranscribeAsync"/> after its token is linked and just before the
    /// lock is asked for, so a test can put a stop's release exactly there. The service never sets it.
    /// </summary>
    internal Action? BeforeTheLock { get; init; }

    /// <summary>Whether whisper.cpp's context is in memory right now. A fact about this machine, for the diagnostics panel.</summary>
    public bool Loaded => _processor is not null;

    /// <summary>How long with nothing to transcribe before the model is released.</summary>
    public TimeSpan UnloadAfter { get; }

    /// <summary>The model in use, for the log and the diagnostics panel. A name, never content.</summary>
    public string ModelName => _model.Name;

    /// <summary>How far the download has got, or null once it is done.</summary>
    public DownloadProgress? Downloading { get; private set; }

    /// <summary>
    /// Why there is no model, once something has tried to get one. Null while it is still working or
    /// once it has succeeded.
    ///
    /// A sentence for the log, never content: what reaches here is a host name the guard refused or the
    /// name of an exception, both of which are facts about this machine rather than about a customer's
    /// screen (INV-10). It exists because "no narration" was previously indistinguishable from "nobody
    /// spoke". The one failure that actually happens, an egress refusal, threw past the catch below and
    /// faulted a task nobody awaited (2026-09-20 review).
    /// </summary>
    public string? Unavailable { get; private set; }

    /// <summary>
    /// Fetches the model if it is not already on disk and loads it. Safe to call again; returns false
    /// when the model could not be had, which is a session without narration rather than a failure.
    /// </summary>
    public async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        if (Ready)
        {
            return true;
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Ready)
            {
                return true;
            }

            var progress = new Progress<DownloadProgress>(p => Downloading = p);
            if (!await _download.EnsureAsync(_model, _path, progress, ct).ConfigureAwait(false))
            {
                Unavailable = "the model could not be downloaded";
                return false;
            }

            Downloading = null;
            _ = Load();
            _loadable = true;
            Unavailable = null;
            return true;
        }
        catch (EgressBlockedException blocked)
        {
            // The one failure that actually happened, and the one this catch did not name. The model
            // host answers 302 to a CDN that was not on the allowlist, so the hop was refused, and an
            // EgressBlockedException thrown from here faulted the un-awaited task in CaptureHost that
            // called this, which is how narration came to fail without a single line anywhere saying so
            // (2026-09-20 review).
            //
            // Its message names the host and nothing else, which is exactly what somebody fixing the
            // allowlist needs.
            Unavailable = blocked.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or ModelIntegrityException or HttpRequestException or InvalidOperationException or WhisperModelLoadException)
        {
            // No model, so no narration. Everything else about the session carries on.
            Unavailable = ex.GetType().Name;
            return false;
        }
        finally
        {
            _idle.Touch();
            _one.Release();
        }
    }

    public async Task<Transcribed?> TranscribeAsync(
        SpeechSegment segment,
        ReadOnlyMemory<short> audio,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!_loadable || audio.IsEmpty)
        {
            return null;
        }

        var samples = new float[audio.Length];
        var source = audio.Span;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = source[i] / 32768f;
        }

        // The caller's token, and the service stopping: either one aborts the native call (ST-112).
        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        }
        catch (ObjectDisposedException)
        {
            // Disposed and finished with; nothing more is transcribed.
            return null;
        }

        using var aborting = linked;
        ct = aborting.Token;
        BeforeTheLock?.Invoke();
        try
        {
            await _one.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The stop's release finished, and disposed the lock, between the token being linked above and
            // the lock being asked for. That is the service stopping, not a fault: nothing more is
            // transcribed, as when the token could not be linked, and the recorder is not told a failure
            // (#262 follow-up).
            return null;
        }

        try
        {
            if (Disposed)
            {
                // Disposal released the model while this call waited for the lock. Loading it again
                // here would leave a native context nobody frees.
                return null;
            }

            // Released after an idle stretch. The file is on disk and was verified when it landed, so
            // this is a reload, not a download.
            var processor = _processor ?? Load();

            var text = new System.Text.StringBuilder();
            double probability = 0;
            var pieces = 0;
            await foreach (var piece in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                if (text.Length > 0)
                {
                    _ = text.Append(' ');
                }

                _ = text.Append(piece.Text.Trim());
                probability += piece.Probability;
                pieces++;
            }

            return pieces == 0
                ? null

                // The mean across the pieces the model produced. TranscriptAssembler decides what to do
                // with a low one; this only reports it.
                : new Transcribed(segment, text.ToString(), probability / pieces);
        }
        catch (Exception ex) when (ex is IOException or WhisperModelLoadException)
        {
            // The file has gone from under us since it was verified: deleted, or the disk is failing.
            // Narration ends for the life of the service with a reason, as a model that never arrived
            // does, rather than every segment from here on paying for a load that cannot succeed.
            Unavailable = ex.GetType().Name;
            _loadable = false;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ApplicationException or WhisperProcessingException)
        {
            // A model that will not run on this frame. One lost segment, not a lost session.
            return null;
        }
        finally
        {
            // Under the lock, so an idle release waiting for it sees this use and leaves the model be.
            _idle.Touch();
            _one.Release();
        }
    }

    /// <summary>
    /// Brings the model into memory. Under <see cref="_one"/>.
    ///
    /// With probabilities: without the flag whisper.cpp reports none, every segment arrived with a
    /// confidence of zero, and the assembler's "unsure" rule dropped every short statement a technician
    /// made (2026-10-12 review).
    /// </summary>
    private WhisperProcessor Load()
    {
        _factory ??= WhisperFactory.FromPath(_path);
        _processor = _factory.CreateBuilder().WithLanguage("en").WithThreads(Threads).WithProbabilities().Build();
        return _processor;
    }

    /// <summary>Lets the context and the model go. Under <see cref="_one"/>.</summary>
    private async ValueTask UnloadAsync()
    {
        if (_processor is { } processor)
        {
            _processor = null;
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        _factory?.Dispose();
        _factory = null;
    }

    /// <summary>
    /// The idle timer ran out. Nothing is released until the lock is held and the use count is still the
    /// one the timer saw: a segment that arrived in between has re-armed the timer, and the model stays.
    /// </summary>
    private async Task UnloadIfIdleAsync(long usesWhenFired)
    {
        if (Disposed)
        {
            return;
        }

        try
        {
            await _one.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the timer firing and the lock. Disposal released everything.
            return;
        }

        try
        {
            if (!Disposed && usesWhenFired == _idle.Uses)
            {
                await UnloadAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _one.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposal took the lock after this one and has finished with it. Nobody awaits this task,
                // so the release is the last thing it does either way.
            }
        }
    }

    /// <summary>
    /// Lets go of the model, without ever holding the service's stop on a native call (ST-112).
    ///
    /// A transcription in flight is told to abort first. The release then waits for it, so the native
    /// context is never freed under a segment or an idle release, but the stop waits for the release only
    /// for <see cref="StopBound.Default"/>. If the native call is still running after that, the release
    /// carries on by itself and frees the context and the model when the call returns, or the process
    /// exiting frees them; either way they are freed once, and only after nothing is using them. The
    /// segment is dropped by the recorder, not half-kept (owner's decision, 2026-11-05).
    ///
    /// Idle, the release finishes at once and frees everything here, as it always did.
    ///
    /// Safe to call more than once and from several threads at once (#262 follow-up). Exactly one caller
    /// runs the release; the others wait for that caller's disposal, which is bounded the same way, and
    /// never cancel, release or dispose anything a second time. The check used to be a flag read and then
    /// set, so two callers arriving together could both pass it: the second then cancelled a token source
    /// the first had disposed, or waited on a lock the first had disposed, and threw.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            await _disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _idle.Dispose();
            await _stopping.CancelAsync().ConfigureAwait(false);

            if (!await StopBound.WithinAsync(ReleaseAsync(), _stopWait, _time).ConfigureAwait(false))
            {
                _abandoned?.Invoke();
            }
        }
        finally
        {
            _ = _disposal.TrySetResult();
        }
    }

    /// <summary>The whole release, under the lock, run to the end whether or not the stop waits for it.</summary>
    private async Task ReleaseAsync()
    {
        // Waits for a segment or an idle release in flight, so the native context is never freed under
        // either of them.
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadAsync().ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
            _one.Dispose();
            _stopping.Dispose();
        }
    }
}
