using System.Buffers.Binary;

namespace ScreenTail.Core.Speech;

/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="Channels">1 or 2. Anything else is not something a capture endpoint produces.</param>
/// <param name="BitsPerSample">16 for PCM, 32 for IEEE float.</param>
/// <param name="IsFloat">True for IEEE float, which is what a shared-mode endpoint usually gives.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>
/// Turns whatever the capture device produces into the 16 kHz mono 16-bit the model wants (ST-027).
///
/// WASAPI in shared mode hands over the device's configured format, commonly 44.1 or 48 kHz stereo
/// 32-bit float, and asking it for something else is a request it may refuse. Converting here rather
/// than relying on the driver keeps that failure out of the capture path, and keeps the arithmetic
/// somewhere it can be checked without a sound card.
///
/// Getting it wrong is not a quiet failure. A channel count read wrong turns a voice into a chipmunk and
/// a sample rate read wrong turns it into a drone, and a transcriber answers both with confident
/// sentences rather than with nothing — which is how invented text reaches a customer's ticket note.
/// </summary>
public static class AudioConversion
{
    /// <summary>What Whisper wants, and the only format anything above this layer ever sees.</summary>
    public const int TargetRate = 16_000;

    /// <returns>16-bit mono samples at 16 kHz, or empty for a format we do not understand.</returns>
    public static ReadOnlyMemory<short> ToMono16k(ReadOnlySpan<byte> data, AudioFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var supported = format.Channels is 1 or 2
            && (format.IsFloat ? format.BitsPerSample == 32 : format.BitsPerSample == 16)
            && format.SampleRate > 0;
        if (!supported || data.IsEmpty)
        {
            return ReadOnlyMemory<short>.Empty;
        }

        // Whole frames only. A truncated read would otherwise be interpreted as a sample built from one
        // byte of one and one byte of the next.
        var frameBytes = bytesPerSample * format.Channels;
        var frames = data.Length / frameBytes;
        if (frames == 0)
        {
            return ReadOnlyMemory<short>.Empty;
        }

        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var at = (frame * format.Channels + channel) * bytesPerSample;
                sum += format.IsFloat
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[at..]))
                    : BinaryPrimitives.ReadInt16LittleEndian(data[at..]) / 32768f;
            }

            // Averaged rather than taking one side: a technician speaking into one half of a headset
            // would otherwise be dropped entirely.
            mono[frame] = sum / format.Channels;
        }

        return Resample(mono, format.SampleRate);
    }

    /// <summary>
    /// Linear interpolation down to 16 kHz.
    ///
    /// Enough for speech at these ratios, and chosen over a filtered resampler deliberately: this runs on
    /// every audio buffer for the length of a support session, inside a 15% CPU budget shared with
    /// screenshots and OCR, and the aliasing it leaves above 8 kHz is above where speech carries meaning.
    /// </summary>
    private static ReadOnlyMemory<short> Resample(float[] mono, int from)
    {
        if (from == TargetRate)
        {
            var same = new short[mono.Length];
            for (var i = 0; i < mono.Length; i++)
            {
                same[i] = Clamp(mono[i]);
            }

            return same;
        }

        var ratio = (double)from / TargetRate;
        var length = (int)(mono.Length / ratio);
        var output = new short[Math.Max(0, length)];
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * ratio;
            var left = (int)position;
            var right = Math.Min(left + 1, mono.Length - 1);
            var fraction = (float)(position - left);
            output[i] = Clamp(mono[left] + ((mono[right] - mono[left]) * fraction));
        }

        return output;
    }

    /// <summary>
    /// Clamped, not wrapped. A device with gain applied hands back samples outside [-1, 1], and wrapping
    /// would turn the loudest moment of a sentence into its opposite — a click, which transcribes as a
    /// word boundary that was never spoken.
    /// </summary>
    private static short Clamp(float sample) =>
        // Scaled by 32768 and clamped, not by 32767. Sixteen-bit audio is asymmetric — one more step
        // below zero than above — so dividing by 32768 on the way in and multiplying by 32767 on the way
        // out loses a bit of every sample and never quite returns what it was given.
        (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue);
}
