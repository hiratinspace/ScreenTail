using System.Runtime.Versioning;
using ScreenTail.Service.Speech;

namespace ScreenTail.Tests.Windows.Speech;

/// <summary>
/// The real capture device, as far as a test machine can be asked about it (ST-027, 2026-10-12 review
/// E-48 and T-236).
///
/// The recorder opens the microphone once per recording stretch and closes it by disposing the
/// enumerator. The device each stretch opened used to be kept in a field and disposed only with the
/// microphone, so every pause left the previous one open. The Mac suite pins that the recorder disposes
/// the enumerator; this pins that the enumerator closes the device, on whatever machine runs it.
///
/// It does not skip. The hosted runner has no capture endpoint, and on it this proves that a machine
/// with no microphone enumerates to nothing, twice, without an error (AC2). The laptop has one, and on
/// it this proves that the device delivers audio, is let go of, and delivers again when opened again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophoneTests
{
    [Fact]
    public async Task ListeningTwiceInARowOpensAndClosesTheDeviceEachTime()
    {
        await using var microphone = new WindowsMicrophone();

        for (var round = 1; round <= 2; round++)
        {
            var chunks = await ListenBrieflyAsync(microphone);

            if (microphone.DeviceName is null)
            {
                Assert.Equal(0, chunks);
            }
            else
            {
                Assert.True(chunks > 0, $"round {round}: the default capture device delivered nothing in two seconds");
            }
        }
    }

    [Fact]
    public async Task DisposingTheMicrophoneIsHarmlessWhateverItHeard()
    {
        // Nothing is held between listens, so disposing it before, between and after is the same no-op.
        var microphone = new WindowsMicrophone();
        await microphone.DisposeAsync();

        _ = await ListenBrieflyAsync(microphone);

        await microphone.DisposeAsync();
        await microphone.DisposeAsync();
    }

    /// <summary>Takes a few buffers and lets the enumerator go, which is how the recorder ends a stretch.</summary>
    private static async Task<int> ListenBrieflyAsync(WindowsMicrophone microphone)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var chunks = 0;
        try
        {
            await foreach (var _ in microphone.ListenAsync(stop.Token))
            {
                if (++chunks >= 3)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The device opened and delivered nothing in time. The caller decides what that means here.
        }

        return chunks;
    }
}
