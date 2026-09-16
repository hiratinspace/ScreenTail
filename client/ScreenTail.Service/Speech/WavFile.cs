using System.Buffers.Binary;
using ScreenTail.Core.Speech;

namespace ScreenTail.Service.Speech;

/// <summary>
/// Reads a WAV file into the format the pipeline uses (ST-027).
///
/// Only for <c>--transcribe</c>, the developer command that measures word error rate against a recorded
/// fixture. Nothing in a session ever reads audio from disk: a session's audio lives in a ring buffer for
/// a few seconds and is never written anywhere.
/// </summary>
public static class WavFile
{
    /// <summary>Reads a RIFF/WAVE file and converts it to 16 kHz mono, or throws if it is not one.</summary>
    public static ReadOnlyMemory<short> ReadMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44
            || !"RIFF".Equals(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), StringComparison.Ordinal)
            || !"WAVE".Equals(System.Text.Encoding.ASCII.GetString(bytes, 8, 4), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{path} is not a WAV file.");
        }

        AudioFormat? format = null;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            var body = at + 8;
            if (size < 0 || body + size > bytes.Length)
            {
                break;
            }

            if (id == "fmt ")
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                format = new AudioFormat(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14)),
                    tag == 3);
            }
            else if (id == "data" && format is { } known)
            {
                return AudioConversion.ToMono16k(bytes.AsSpan(body, size), known);
            }

            at = body + size + (size % 2);
        }

        throw new InvalidDataException($"{path} has no readable fmt and data chunks.");
    }
}
