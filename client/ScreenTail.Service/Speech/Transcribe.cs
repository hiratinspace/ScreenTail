using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Speech;

/// <summary>
/// Runs a recorded WAV file through the real speech pipeline and prints what it heard (ST-027).
///
/// The whole pipeline, not a shortcut: the same voice-activity detector, the same gate, the same model
/// and the same assembler a session uses. A word error rate measured against anything less would be a
/// number about a program we do not ship.
///
/// It also reports the ratio of audio length to processing time, which is AC4's lag criterion in the form
/// that can be measured without sitting in front of the machine: a pipeline that transcribes a ten-minute
/// recording in two minutes keeps up with a live session on the same hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Transcribe
{
    public static async Task<int> RunAsync(string path, SpeechModel model, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"No recording at {path}.").ConfigureAwait(false);
            return 2;
        }

        ReadOnlyMemory<short> audio;
        try
        {
            audio = WavFile.ReadMono16k(path);
        }
        catch (InvalidDataException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "Convert it first: ffmpeg -i in.m4a -ac 1 -ar 16000 -c:a pcm_s16le out.wav").ConfigureAwait(false);
            return 2;
        }

        var seconds = audio.Length / (double)AudioConversion.TargetRate;
        await Console.Error.WriteLineAsync(
            string.Create(CultureInfo.InvariantCulture, $"{path}: {seconds:F1}s of audio, model {model.Name}")).ConfigureAwait(false);

        await using var recogniser = new WhisperRecogniser(new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy()))), model);
        if (!await recogniser.PrepareAsync(ct).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync($"Could not prepare the {model.Name} model.").ConfigureAwait(false);
            return 1;
        }

        var heard = new List<TranscriptSegment>();
        var clock = Stopwatch.StartNew();
        var recorder = new NarrationRecorder(
            new FileMicrophone(audio),
            recogniser,
            (segment, _) =>
            {
                heard.Add(segment);
                return Task.FromResult(true);
            });

        await recorder.RunAsync(ct).ConfigureAwait(false);
        clock.Stop();

        foreach (var segment in heard)
        {
            Console.WriteLine(segment.Text);
        }

        var ratio = clock.Elapsed.TotalSeconds / Math.Max(seconds, 0.001);
        await Console.Error.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{heard.Count} segments, {recorder.Rejected} rejected as invented, {recorder.Heard} stretches heard. "
            + $"Took {clock.Elapsed.TotalSeconds:F1}s for {seconds:F1}s of audio ({ratio:F2}x real time; under 1.00 keeps up live).")).ConfigureAwait(false);

        return heard.Count > 0 ? 0 : 1;
    }

    /// <summary>Feeds a recording through the pipeline as though it were arriving from a microphone.</summary>
    private sealed class FileMicrophone(ReadOnlyMemory<short> audio) : IMicrophone
    {
        public string? DeviceName => "file";

        public int SampleRate => AudioConversion.TargetRate;

        public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Chunked the way a capture device delivers, so the gate sees the same frame boundaries it
            // would in a session rather than one enormous buffer.
            const int Chunk = AudioConversion.TargetRate / 10;
            for (var at = 0; at < audio.Length; at += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                yield return audio.Slice(at, Math.Min(Chunk, audio.Length - at));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
