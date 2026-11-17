using System.Runtime.Versioning;
using ScreenTail.Core.Speech;
using ScreenTail.Service.Speech;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// Speech for the processor breakdown (ST-031), made on the laptop by Windows' own voice, because CI has
/// nobody talking and the model's cost depends on there being words to decode.
///
/// The words are this file's own script about a printer and a disk, never anything from a session. They
/// are compared with what the model heard to give a word error rate, a number; neither the script nor the
/// transcript is ever written to a report (INV-10 is about content, and the habit is kept here too).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class SpokenAudio
{
    public const int SampleRate = 16_000;

    /// <summary>A technician's running account of a fix, one sentence at a time.</summary>
    public static IReadOnlyList<string> Script { get; } =
    [
        "I am opening the services console to check whether the print spooler is running.",
        "The spooler is stopped, so I will start it and set it to start automatically.",
        "Now I am clearing the stuck jobs out of the queue folder on this workstation.",
        "Next I restart the machine and send a test page to the second floor printer.",
        "The test page printed, so the ticket can be closed once the user confirms it.",
        "I am checking the event log for disk warnings from last night.",
        "There are three warnings about the backup volume running low on space.",
        "I will extend the volume and schedule a cleanup of the old restore points.",
    ];

    /// <summary>Whether this machine has a voice to speak with.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return SpeechSynthesizer.AllVoices.Count > 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The English voices this machine has, at most <paramref name="most"/>, the default first. More than
    /// one voice is more than one speaker for the word error comparison; all of them are still synthetic.
    /// </summary>
    public static IReadOnlyList<VoiceInformation> EnglishVoices(int most)
    {
        var voices = SpeechSynthesizer.AllVoices
            .Where(voice => voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(voice => voice.Id == SpeechSynthesizer.DefaultVoice?.Id)
            .Take(most)
            .ToList();
        return voices.Count > 0 ? voices : [.. SpeechSynthesizer.AllVoices.Take(1)];
    }

    /// <summary>One sentence, spoken, as 16 kHz mono samples, in the default voice or the one given.</summary>
    public static async Task<short[]> SpeakAsync(string text, string folder, CancellationToken ct, VoiceInformation? voice = null)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (voice is not null)
        {
            synthesizer.Voice = voice;
        }

        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            _ = await reader.LoadAsync((uint)stream.Size).AsTask(ct);
            reader.ReadBytes(bytes);
        }

        // Through the service's own WAV reader, so the conversion to 16 kHz mono is the one it uses.
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, bytes, ct);
        try
        {
            return WavFile.ReadMono16k(path).ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Quiet room tone: what a microphone hears between sentences. Never digital silence.</summary>
    public static short[] RoomTone(double seconds, int seed)
    {
        var random = new Random(seed);
        var samples = new short[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)random.Next(-60, 61);
        }

        return samples;
    }

    /// <summary>Clips joined end to end, with <paramref name="gapSeconds"/> of room tone between them.</summary>
    public static short[] Join(IEnumerable<short[]> clips, double gapSeconds, int seed)
    {
        var joined = new List<short>();
        var first = true;
        foreach (var clip in clips)
        {
            if (!first)
            {
                joined.AddRange(RoomTone(gapSeconds, seed++));
            }

            joined.AddRange(clip);
            first = false;
        }

        return [.. joined];
    }

    /// <summary>Room tone added under speech, so the recording sounds like a room and not like a file.</summary>
    public static short[] WithRoomTone(short[] speech, int seed)
    {
        var tone = RoomTone(speech.Length / (double)SampleRate + 0.01, seed);
        var mixed = new short[speech.Length];
        for (var i = 0; i < speech.Length; i++)
        {
            mixed[i] = (short)Math.Clamp(speech[i] + tone[i], short.MinValue, short.MaxValue);
        }

        return mixed;
    }

    /// <summary>Word error rate of <paramref name="heard"/> against <paramref name="said"/>: a number, never the words.</summary>
    public static double WordErrorRate(string said, string heard)
    {
        var reference = Words(said);
        var hypothesis = Words(heard);
        if (reference.Length == 0)
        {
            return hypothesis.Length == 0 ? 0 : 1;
        }

        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                var substitution = previous[j - 1] + (string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[hypothesis.Length] / (double)reference.Length;
    }

    private static string[] Words(string text) =>
        [.. text.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ')
            .Aggregate(new System.Text.StringBuilder(), (b, c) => b.Append(c))
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)];
}

/// <summary>
/// A microphone that plays a script in real time: room tone, then each clip followed by a pause, round
/// and round, for as long as the recorder listens. What the recorder does with it (the gate, the ring,
/// the queue, the model) is the service's own code.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class ScriptedMicrophone(IReadOnlyList<short[]> clips, double pauseSeconds) : IMicrophone
{
    private static readonly TimeSpan Chunk = TimeSpan.FromMilliseconds(100);

    public string? DeviceName => "scripted";

    public int SampleRate => SpokenAudio.SampleRate;

    /// <summary>Seconds of speech handed to the recorder so far, and seconds in all.</summary>
    public (double Speech, double All) Played => (Volatile.Read(ref _speechSamples) / (double)SampleRate, Volatile.Read(ref _allSamples) / (double)SampleRate);

    private long _speechSamples;
    private long _allSamples;

    public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long sent = 0;
        var seed = 1;
        foreach (var (samples, speech) in Program())
        {
            var at = 0;
            while (at < samples.Length)
            {
                // Paced by the clock rather than by the delay, so a late wake sends more, never less.
                var due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
                if (due <= sent)
                {
                    await Task.Delay(Chunk, ct);
                    continue;
                }

                var take = (int)Math.Min(samples.Length - at, due - sent);
                yield return samples.AsMemory(at, take);
                at += take;
                sent += take;
                _ = Interlocked.Add(ref _allSamples, take);
                if (speech)
                {
                    _ = Interlocked.Add(ref _speechSamples, take);
                }
            }
        }

        IEnumerable<(short[] Samples, bool Speech)> Program()
        {
            // Two seconds for the detector to learn the room, as a real microphone gives it.
            yield return (SpokenAudio.RoomTone(2, seed++), false);
            while (true)
            {
                if (clips.Count == 0)
                {
                    yield return (SpokenAudio.RoomTone(5, seed++), false);
                    continue;
                }

                foreach (var clip in clips)
                {
                    yield return (clip, true);
                    yield return (SpokenAudio.RoomTone(pauseSeconds, seed++), false);
                }
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
