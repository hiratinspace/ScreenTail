using System.Runtime.Versioning;
using ScreenTail.Core.Speech;
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
    public void TheDevicesAreTheActiveEndpointsWithOneDefaultAndTheNameIsTheDefaults()
    {
        // ST-080 AC3. On the hosted runner there are none and the name is null; on the laptop the
        // default communications device is marked, once, and it is the one a listen with no choice opens.
        var microphone = new WindowsMicrophone();

        var devices = microphone.Devices();

        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
        // Ids, not names: a failure prints both sides into a public Actions log, and a name can be personal.
        Assert.Equal(MicrophoneChoice.Resolve(devices, null).Device?.Id, microphone.DeviceId);
        Assert.Equal(microphone.DeviceId is null, microphone.DeviceName is null);
    }

    [Fact]
    public async Task AChosenDeviceThatIsNotConnectedFallsBackToTheDefaultAndStillListens()
    {
        // A headset left at home is not a session without narration: the default opens in its place.
        await using var microphone = new WindowsMicrophone(() => "{0.0.1.00000000}.{not-a-device-anyone-has}");
        var fallback = MicrophoneChoice.Resolve(microphone.Devices(), null).Device;

        var chunks = await ListenBrieflyAsync(microphone);

        Assert.Equal(fallback?.Id, microphone.DeviceId);
        if (fallback is null)
        {
            Assert.Equal(0, chunks);
        }
        else
        {
            Assert.True(chunks > 0, "the default capture device delivered nothing in two seconds in place of a missing choice");
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
