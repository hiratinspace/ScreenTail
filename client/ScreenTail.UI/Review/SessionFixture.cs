using System.IO;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>
/// The hand-drawn session the screenshot harness renders (ST-006), found by walking up from the binary
/// to the repository. Never substituted: a harness that quietly invents a sample goes green while
/// proving nothing.
/// </summary>
internal static class SessionFixture
{
    public const string RelativePath = "research/fixtures/handcrafted/spooler-stopped-screenconnect";

    public static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RelativePath);
            if (File.Exists(Path.Combine(candidate, "session.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No {RelativePath}/session.json above {AppContext.BaseDirectory}.");
    }

    public static Session Load() => SessionJson.Deserialize(File.ReadAllText(Path.Combine(Directory(), "session.json")));
}

/// <summary>
/// The fixture's frames, read from its folder, for the harness. Edits go nowhere: a blur hands back the
/// same bytes, a delete says yes. The running application never sees this class.
/// </summary>
internal sealed class FixtureFrames(string directory) : IReviewFrames
{
    public async Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var path = Path.Combine(directory, frame.Image);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
    }

    public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) =>
        ImageAsync(new Frame
        {
            Id = frameId,
            TsMs = 0,
            Trigger = FrameTrigger.Click,
            Image = $"frames/{frameId}.png",
            Width = 1,
            Height = 1,
            RedactionPending = false,
            RedactedAt = DateTimeOffset.UnixEpoch,
            MaskedRegions = [],
            SensitiveContext = false,
            ExcludedByUser = false,
        }, ct);
}
