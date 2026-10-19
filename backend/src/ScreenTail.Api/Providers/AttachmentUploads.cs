using System.Text.Json;

namespace ScreenTail.Api.Providers;

/// <summary>
/// The screenshots that follow a note or an article once it has landed, a few at a time, and what did
/// not attach (2026-10-12 review, S-69 and E-42).
///
/// Shared by ConnectWise and Hudu so they cannot disagree about the one rule that matters here: the
/// note or article is already there, so a screenshot that did not attach is a success that says what is
/// missing, never a failure. A failure is what the client's Retry sends again, and sending the note
/// again is the duplicate ST-094 forbids.
///
/// A few go at once so twenty-five screenshots fit inside the publish deadline; only a few, so one
/// publish is not a burst that the provider's rate limit answers. One refused upload does not stop the
/// others. When the publish runs out of time, the uploads still waiting are counted as missing rather
/// than lost in a cancellation, and those already on their way, like one whose answer could not be read
/// or that the provider answered as <see cref="ProviderErrorKind.Unknown"/>, as possibly there: the
/// advice is to look for them before adding them by hand.
/// </summary>
internal static class AttachmentUploads
{
    public const int AtOnce = 4;

    /// <param name="upload">Sends one attachment and answers as the provider does.</param>
    /// <param name="landed">What already happened, as the start of a sentence: "The note was published as 90001".</param>
    /// <param name="todo">What to do instead of publishing again.</param>
    /// <returns>Null when every attachment went; otherwise what is missing, in Spec §4's two sentences.</returns>
    public static async Task<ProviderError?> UploadAsync(
        IReadOnlyList<NoteAttachment> attachments,
        Func<NoteAttachment, CancellationToken, Task<ProviderResult<JsonElement>>> upload,
        string landed,
        string todo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(upload);
        if (attachments.Count == 0)
        {
            return null;
        }

        // Per screenshot: why it is missing, if it is, and whether it had left by then. One that was on
        // its way when the deadline came, or whose answer could not be read, may be there after all.
        var failed = new ProviderError?[attachments.Count];
        var unsure = new bool[attachments.Count];
        using var gate = new SemaphoreSlim(AtOnce);
        await Task.WhenAll(attachments.Select(async (attachment, i) =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    unsure[i] = true;
                    failed[i] = (await upload(attachment, ct).ConfigureAwait(false)).Error;

                    // An Unknown answer is the provider saying the upload left and nothing said whether it
                    // was saved, so it may be there like one whose answer could not be read.
                    unsure[i] = failed[i] is { Kind: ProviderErrorKind.Unknown };
                }
                finally
                {
                    _ = gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                failed[i] = new ProviderError(ProviderErrorKind.Unavailable, unsure[i] ? "The publish ran out of time while it was uploading." : "The publish ran out of time first.", todo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Sent, and the answer could not be read. The note is still there, and so are the others.
                unsure[i] = true;
                failed[i] = new ProviderError(ProviderErrorKind.Unavailable, "Its answer could not be read.", todo);
            }
        })).ConfigureAwait(false);

        var missing = Enumerable.Range(0, attachments.Count).Where(i => failed[i] is not null && !unsure[i]).ToList();
        var maybe = Enumerable.Range(0, attachments.Count).Where(i => failed[i] is not null && unsure[i]).ToList();
        if (missing.Count == 0 && maybe.Count == 0)
        {
            return null;
        }

        string Names(List<int> which) => string.Join(", ", which.Select(i => attachments[i].FileName));
        var first = failed[missing.Count > 0 ? missing[0] : maybe[0]]!;
        var what = (missing.Count, maybe.Count) switch
        {
            (_, 0) => $"{missing.Count} of {attachments.Count} screenshots did not attach ({Names(missing)})",
            (0, _) => $"{maybe.Count} of {attachments.Count} screenshots may not have attached ({Names(maybe)})",
            _ => $"{missing.Count} of {attachments.Count} screenshots did not attach ({Names(missing)}) and {maybe.Count} may not have ({Names(maybe)})",
        };

        // Adding by hand one that is already there is a duplicate of its own, so those are looked for first.
        var look = maybe.Count == 0
            ? string.Empty
            : maybe.Count == 1
                ? $" Look for {Names(maybe)} before adding it: it was on its way and may be there already."
                : $" Look for {Names(maybe)} before adding them: they were on their way and may be there already.";
        return new ProviderError(first.Kind, $"{landed}, but {what}: {first.What}", todo + look);
    }
}
