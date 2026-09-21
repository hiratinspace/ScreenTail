using System.Runtime.Versioning;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using Whisper.net;

namespace ScreenTail.Service.Speech;

/// <summary>
/// Local transcription with whisper.cpp (ST-027).
///
/// <b>Local, and not as a configuration choice.</b> A transcript of a support call is the most sensitive
/// thing a session produces — it is the technician saying out loud what they are doing to a customer's
/// machine — so it never leaves the device whatever the tenant's egress settings say. The only thing that
/// crosses the network here is the model file, which is public, hash-checked and not customer data.
///
/// The model is fetched on first use and can be hundreds of megabytes. <see cref="Ready"/> stays false
/// until it has landed and the factory has loaded it; speech heard before then is dropped and counted by
/// the recorder rather than queued, because a queue that grew for a whole session would then try to
/// transcribe an hour of audio at once (AC3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WhisperRecogniser : ISpeechRecogniser
{
    /// <summary>
    /// How many threads whisper.cpp may use.
    ///
    /// Unset, it takes every hardware thread the machine has — eight on the reference laptop, which is
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

    private readonly SpeechModel _model;
    private readonly string _path;
    private readonly ModelDownload _download;

    /// <summary>
    /// One at a time. whisper.cpp holds state per processor, and two overlapping calls on one processor
    /// interleave into a transcript of neither.
    /// </summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperRecogniser(ModelDownload download, SpeechModel? model = null, string? directory = null)
    {
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _model = model ?? SpeechModels.For(Environment.ProcessorCount);
        _path = Path.Combine(
            directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTail",
                "models"),
            $"ggml-{_model.Name}.bin");
    }

    public bool Ready => _processor is not null;

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
    /// spoke" — the one failure that actually happens, an egress refusal, threw past the catch below and
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
            _factory = WhisperFactory.FromPath(_path);
            _processor = _factory.CreateBuilder().WithLanguage("en").WithThreads(Threads).Build();
            Unavailable = null;
            return true;
        }
        catch (EgressBlockedException blocked)
        {
            // The one failure that actually happened, and the one this catch did not name. The model
            // host answers 302 to a CDN that was not on the allowlist, so the hop was refused — and an
            // EgressBlockedException thrown from here faulted the un-awaited task in CaptureHost that
            // called this, which is how narration came to fail without a single line anywhere saying so
            // (2026-09-20 review).
            //
            // Its message names the host and nothing else, which is exactly what somebody fixing the
            // allowlist needs.
            Unavailable = blocked.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or ModelIntegrityException or HttpRequestException or InvalidOperationException)
        {
            // No model, so no narration. Everything else about the session carries on.
            Unavailable = ex.GetType().Name;
            return false;
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<Transcribed?> TranscribeAsync(
        SpeechSegment segment,
        ReadOnlyMemory<short> audio,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var processor = _processor;
        if (processor is null || audio.IsEmpty)
        {
            return null;
        }

        var samples = new float[audio.Length];
        var source = audio.Span;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = source[i] / 32768f;
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        catch (Exception ex) when (ex is InvalidOperationException or ApplicationException)
        {
            // A model that will not run on this frame. One lost segment, not a lost session.
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        _factory?.Dispose();
        _one.Dispose();
    }
}
