using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;
using ScreenTail.Spike.Core;
using Whisper.net;

namespace ScreenTail.Spike.Capture;

/// <summary>
/// Real STT load for AC1: default microphone (technician only, INV-9) → 5 s chunks → Whisper base.
/// With no usable mic, synthetic audio keeps the same CPU load. Transcript text is counted, never stored.
/// </summary>
internal sealed class WhisperLoad : IDisposable
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = SampleRate * 5;

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly RunReport _report;
    private readonly Channel<float[]> _chunks = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly List<float> _pending = new(ChunkSamples);
    private WaveInEvent? _waveIn;

    private WhisperLoad(WhisperFactory factory, WhisperProcessor processor, RunReport report)
    {
        _factory = factory;
        _processor = processor;
        _report = report;
    }

    public static async Task<WhisperLoad?> CreateAsync(RunReport report)
    {
        try
        {
            var factory = WhisperFactory.FromPath(await Assets.EnsureWhisperModelAsync());
            var processor = factory.CreateBuilder().WithLanguage("en").Build();
            return new WhisperLoad(factory, processor, report);
        }
        catch (Exception ex)
        {
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"Whisper disabled — {reason}");
            report.WhisperUnavailable = reason;
            return null;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        StartAudio(ct);
        try
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(ct))
            {
                var started = Stopwatch.GetTimestamp();
                var words = 0;
                await foreach (var segment in _processor.ProcessAsync(chunk, ct))
                {
                    words += OcrAgreement.Tokenize(segment.Text).Count;
                }

                _report.RecordWhisperChunk(Stopwatch.GetElapsedTime(started).TotalMilliseconds, words);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _waveIn?.StopRecording();
        }
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _processor.Dispose();
        _factory.Dispose();
    }

    private void StartAudio(CancellationToken ct)
    {
        if (WaveInEvent.DeviceCount > 0)
        {
            try
            {
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 100 };
                _waveIn.DataAvailable += OnAudio;
                _waveIn.StartRecording();
                _report.AudioSource = $"default microphone ({WaveInEvent.DeviceCount} input device(s))";
                return;
            }
            catch (Exception ex)
            {
                // Typically Windows privacy settings blocking desktop apps from the mic.
                _waveIn?.Dispose();
                _waveIn = null;
                _report.AudioSource = $"microphone failed ({ex.GetType().Name}: {ex.Message}); synthetic audio";
            }
        }
        else
        {
            _report.AudioSource = "no microphone; synthetic audio";
        }

        _ = FeedSyntheticAsync(ct);
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            _pending.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
        }

        if (_pending.Count >= ChunkSamples)
        {
            if (!_chunks.Writer.TryWrite(_pending.ToArray()))
            {
                _report.WhisperChunksDropped++;
            }

            _pending.Clear();
        }
    }

    private async Task FeedSyntheticAsync(CancellationToken ct)
    {
        var random = new Random(1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var chunk = new float[ChunkSamples];
                for (var i = 0; i < chunk.Length; i++)
                {
                    chunk[i] = (float)((random.NextDouble() * 0.02) - 0.01);
                }

                if (!_chunks.Writer.TryWrite(chunk))
                {
                    _report.WhisperChunksDropped++;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
