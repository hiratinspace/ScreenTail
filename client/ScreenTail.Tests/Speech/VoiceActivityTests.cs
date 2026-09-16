using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027. <see cref="SpeechGate"/> has always taken a probability per frame and nothing produced one.
/// This is that missing half, and it decides how much of a support call is sent to a transcriber at all.
///
/// It is deliberately a signal-to-noise measurement rather than a loudness threshold. A fixed threshold
/// works on the machine it was tuned on and fails everywhere else: a technician on a laptop fan in a
/// server room and a technician in a quiet office have noise floors tens of decibels apart, and a
/// threshold that suits one either transcribes the other's silence or hears nothing at all.
/// </summary>
public sealed class VoiceActivityTests
{
    private const int Rate = 16_000;

    [Fact]
    public void DigitalSilenceIsNeverSpeech()
    {
        var vad = new VoiceActivity();
        var silence = new short[512];

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(0, vad.Offer(silence));
        }
    }

    [Fact]
    public void AVoiceWellAboveTheNoiseFloorIsSpeech()
    {
        var vad = new VoiceActivity();
        Settle(vad, Noise(amplitude: 200));

        Assert.True(vad.Offer(Tone(amplitude: 6000)) > 0.9, "a voice 30 dB over the room should read as speech");
    }

    [Fact]
    public void ASteadyHumIsNotSpeech()
    {
        // The case a loudness threshold gets wrong. A fan, a fluorescent ballast or a busy open-plan
        // office is loud and says nothing, and transcribing it would spend the session's CPU budget
        // inventing text — Whisper's failure mode on non-speech is to produce fluent sentences.
        var vad = new VoiceActivity();
        var hum = Noise(amplitude: 3000);
        Settle(vad, hum);

        Assert.True(vad.Offer(hum) < 0.2, "a steady room tone must not read as speech however loud it is");
    }

    [Fact]
    public void AVoiceInANoisyRoomIsStillSpeech()
    {
        var vad = new VoiceActivity();
        Settle(vad, Noise(amplitude: 3000));

        Assert.True(vad.Offer(Tone(amplitude: 20000)) > 0.9, "a raised voice over a loud room is still speech");
    }

    [Fact]
    public void TheFloorDoesNotClimbWhileSomeoneIsTalking()
    {
        // The bug this rule exists to prevent: if the floor tracked every frame, a long sentence would
        // drag it up behind the speaker and the back half of the sentence would fall below the threshold.
        // A technician explaining what they just did would be transcribed for three seconds and then cut.
        var vad = new VoiceActivity();
        Settle(vad, Noise(amplitude: 200));

        var speaking = Tone(amplitude: 6000);
        double last = 0;
        for (var i = 0; i < 300; i++)
        {
            last = vad.Offer(speaking);
        }

        Assert.True(last > 0.9, $"after ten seconds of speech the probability had fallen to {last:F2}");
    }

    [Fact]
    public void TheFloorFollowsARoomThatGetsQuieter()
    {
        // A machine that starts beside a server rack and is carried to a desk. The floor has to come
        // down, or nothing said afterwards is ever loud enough to count.
        var vad = new VoiceActivity();
        Settle(vad, Noise(amplitude: 8000));
        Settle(vad, Noise(amplitude: 100));

        Assert.True(vad.Offer(Tone(amplitude: 3000)) > 0.9, "the floor never came back down");
    }

    [Fact]
    public void ANoiseThatStartsMidSessionIsEventuallyLearnedRatherThanTranscribedForever()
    {
        // A fan or a drive that spins up after the session began. Nobody talks for a minute without
        // pausing, so a "speech" run this long is a machine, and a floor that refused to learn from it
        // would send every frame from then on to a transcriber that answers non-speech with fluent
        // invented sentences.
        var vad = new VoiceActivity();
        Settle(vad, new short[512]);

        var hum = Noise(amplitude: 4000);
        double last = 1;
        for (var i = 0; i < 4000; i++)
        {
            last = vad.Offer(hum);
        }

        Assert.True(last < 0.2, $"the floor never learned the new room; still reporting {last:F2}");
    }

    [Fact]
    public void AFrameWithNoSamplesIsNotSpeech()
    {
        Assert.Equal(0, new VoiceActivity().Offer([]));
    }

    /// <summary>Feeds enough frames for the floor to learn the room.</summary>
    private static void Settle(VoiceActivity vad, short[] frame)
    {
        for (var i = 0; i < 200; i++)
        {
            _ = vad.Offer(frame);
        }
    }

    /// <summary>Pseudo-random noise: loud, and nothing like a voice.</summary>
    private static short[] Noise(short amplitude)
    {
        var random = new Random(42);
        var frame = new short[Rate / 32];
        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = (short)random.Next(-amplitude, amplitude);
        }

        return frame;
    }

    /// <summary>A 200 Hz tone, which is where a speaking voice lives.</summary>
    private static short[] Tone(short amplitude)
    {
        var frame = new short[Rate / 32];
        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 200 * i / Rate));
        }

        return frame;
    }
}
