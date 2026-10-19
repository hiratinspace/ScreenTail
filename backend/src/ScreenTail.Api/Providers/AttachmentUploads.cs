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
/// than lost in a cancellation.
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

        var failed = new ProviderError?[attachments.Count];
        using var gate = new SemaphoreSlim(AtOnce);
        await Task.WhenAll(attachments.Select(async (attachment, i) =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    failed[i] = (await upload(attachment, ct).ConfigureAwait(false)).Error;
                }
                finally
                {
                    _ = gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                failed[i] = new ProviderError(ProviderErrorKind.Unavailable, "The publish ran out of time first.", todo);
            }
        })).ConfigureAwait(false);

        var missing = Enumerable.Range(0, attachments.Count).Where(i => failed[i] is not null).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        var first = failed[missing[0]]!;
        var names = string.Join(", ", missing.Select(i => attachments[i].FileName));
        return new ProviderError(
            first.Kind,
            $"{landed}, but {missing.Count} of {attachments.Count} screenshots did not attach ({names}): {first.What}",
            todo);
    }
}
