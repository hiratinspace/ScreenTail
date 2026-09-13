using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ScreenTail.Core.Speech;

/// <param name="Name">What Settings calls it: "Small", "Medium" (Spec §5 S5).</param>
/// <param name="Bytes">Expected size, so progress can be a fraction rather than a rising number.</param>
/// <param name="Sha256">What the finished file must hash to. Lower-case hex.</param>
public sealed record SpeechModel(string Name, Uri Source, long Bytes, string Sha256);

/// <param name="Received">Bytes on disk, including anything a previous attempt left.</param>
public sealed record DownloadProgress(long Received, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Received / (double)Total, 0, 1);
}

/// <summary>The file was downloaded but is not the file we asked for.</summary>
public sealed class ModelIntegrityException(string message) : Exception(message);

/// <summary>
/// Fetches a speech model, and picks up where it left off (ST-027).
///
/// The model is hundreds of megabytes and the technician is on a support call. Three things follow, and
/// each is a criterion rather than a nicety: it downloads to a temporary file and only becomes the real
/// one once it is complete and verified, so a half-written model is never loaded; it resumes with a Range
/// request, because starting a 500 MB download again because a laptop slept is the difference between the
/// feature working on a real machine and not; and it reports progress, because "the app is unresponsive"
/// and "the app is downloading something and not saying so" look identical from outside.
///
/// <b>The hash is checked before the file is put in place, not after.</b> A model is executable input to a
/// process that reads a technician's screen — a wrong file is not a broken feature, it is untrusted code
/// weights. A truncated download that happens to be a valid file would otherwise be loaded and used.
/// </summary>
public sealed class ModelDownload(HttpClient client, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How often progress is reported, at most. A callback per chunk would be thousands a second.</summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Makes sure the model is on disk at <paramref name="path"/>, downloading or resuming as needed, and
    /// returns false only when it could not be had. An existing, verified file is left alone.
    /// </summary>
    public async Task<bool> EnsureAsync(
        SpeechModel model,
        string path,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path) && await MatchesAsync(path, model.Sha256, ct).ConfigureAwait(false))
        {
            progress?.Report(new DownloadProgress(model.Bytes, model.Bytes));
            return true;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Beside the real file rather than in the temp directory: a partial download has to survive a
        // reboot to be worth resuming, and it has to land on the same volume so the move is atomic.
        var partial = path + ".partial";
        var already = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (already > model.Bytes)
        {
            // Longer than the model can be, so it is not a prefix of it — a changed model, or a mangled
            // file. Resuming from here would append to rubbish and fail the hash after another 500 MB.
            File.Delete(partial);
            already = 0;
        }

        await FetchAsync(model, partial, already, progress, ct).ConfigureAwait(false);

        if (!await MatchesAsync(partial, model.Sha256, ct).ConfigureAwait(false))
        {
            // Deleted rather than kept: a file that failed its hash is not a head start on the right one,
            // and leaving it would make every later attempt resume from something known to be wrong.
            File.Delete(partial);
            throw new ModelIntegrityException(
                $"The downloaded {model.Name} model does not match its expected hash and has been discarded.");
        }

        File.Move(partial, path, overwrite: true);
        return true;
    }

    private async Task FetchAsync(
        SpeechModel model,
        string partial,
        long from,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.Source);
        if (from > 0)
        {
            request.Headers.Range = new RangeHeaderValue(from, null);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // A server that ignores the Range header answers 200 with the whole file. Appending that to what
        // we already have would produce a file with the first bytes twice, which fails the hash after the
        // whole download — so the local file is dropped and this becomes a fresh one.
        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        if (from > 0 && !resuming)
        {
            from = 0;
        }

        await using var file = new FileStream(
            partial,
            from > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        var received = from;
        var lastReport = _time.GetTimestamp();
        progress?.Report(new DownloadProgress(received, model.Bytes));

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            if (_time.GetElapsedTime(lastReport) >= ReportEvery)
            {
                lastReport = _time.GetTimestamp();
                progress?.Report(new DownloadProgress(received, model.Bytes));
            }
        }

        progress?.Report(new DownloadProgress(received, model.Bytes));
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(hash),
            expected.ToLower(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }
}
