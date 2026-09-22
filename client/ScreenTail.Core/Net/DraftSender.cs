using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTail.Core.Intel;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Net;

/// <summary>
/// Sends a finished session to the backend to be drafted, and keeps what comes back (ST-063, ST-064).
///
/// This is the step that had never run. The bundle has been built for real on every session since
/// ST-060, the backend has drafted and answered since ST-063, and the outbox has queued and retried
/// since ST-064 — and between them sat a stub returning "no summarization provider is configured yet",
/// so no session has ever produced a note.
///
/// <b>The translation is the substance.</b> A <see cref="SessionBundle"/>'s frame carries a path, because
/// that is what a session looks like on disk; the backend needs the bytes. Each selected frame's redacted
/// image is read out of the store and sent base64, and the path is not sent at all — it is a filename
/// from a technician's machine and of no use to a model (INV-10).
///
/// <b>The bundle is rebuilt here rather than queued.</b> The outbox holds a marker, so a draft owed since
/// yesterday is selected from the store as it is now. Retention may have thinned it in between, and a
/// queued bundle could otherwise resurrect a frame the tenant's window has already removed (INV-12).
///
/// <b>Every request goes through the egress guard</b>, because the client the host hands this is built on
/// it. Nothing here decides whether it may send; it decides what to send and what the answer meant.
/// </summary>
/// <param name="token">
/// The device's bearer token, or null when the device is not enrolled. A bundle is a customer's screen,
/// so an unenrolled device waits rather than sending it unauthenticated — and waits rather than failing,
/// because enrolment is a thing that happens later, not a thing that has gone wrong.
/// </param>
public sealed class DraftSender(
    HttpClient http,
    ISessionStore store,
    Func<string?> token,
    BundleOptions? options = null)
{
    /// <summary>Where the backend drafts. Relative, so the host's base address decides the deployment.</summary>
    private static readonly Uri Endpoint = new("v1/sessions/summarize", UriKind.Relative);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Sends one queued draft and says what the outbox should do with it.
    ///
    /// The three answers are different promises. <c>Done</c> means the note is in the store. <c>Pending</c>
    /// means asking again later could work — the budget resets at midnight, the model comes back, a
    /// device gets enrolled. <c>Failed</c> means it could not: a bundle the backend refuses is refused
    /// just as firmly in an hour, and a session retention has taken is not coming back.
    /// </summary>
    public async Task<SendOutcome> SendAsync(OutboxItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (token() is not { Length: > 0 } bearer)
        {
            return SendOutcome.Retry("This device is not enrolled yet, so the session was not sent.");
        }

        var session = await store.LoadSessionAsync(item.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            // Retention took it, or the technician discarded it. Neither is fixed by asking again.
            return SendOutcome.Failed("The session was gone before it could be drafted.");
        }

        var sizes = await store.GetFrameImageSizesAsync(item.SessionId, ct).ConfigureAwait(false);
        var bundle = BundleBuilder.Build(session, sizes, options);
        var wire = await WireAsync(bundle, ct).ConfigureAwait(false);

        using var request = EgressRequest.For(HttpMethod.Post, Endpoint, EgressPurpose.Summarisation);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = JsonContent.Create(wire, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unreachable, or too slow. This is the case the outbox was written for: a draft owed on a
            // train is still owed when the machine comes back.
            //
            // Retried rather than left uncertain, even though a request that timed out may have reached
            // the model. The backend settles a timed-out call at its estimate, so a duplicate is bounded
            // by the same daily cap as everything else — and a missing note costs the technician the
            // session, which is the larger of the two.
            return SendOutcome.Retry("The backend did not answer.");
        }
        catch (EgressBlockedException blocked)
        {
            // Local-only, or a host the tenant does not allow. A policy decision, not a fault, and not
            // something a retry changes until an administrator changes it.
            return SendOutcome.Failed(blocked.Message);
        }

        using (response)
        {
            return await InterpretAsync(response, item.SessionId, ct).ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> InterpretAsync(HttpResponseMessage response, string sessionId, CancellationToken ct)
    {
        var answer = await ReadAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK && answer?.Draft is { } draft)
        {
            await store.SaveDraftAsync(sessionId, draft, ct).ConfigureAwait(false);
            await store.SetSessionStateAsync(sessionId, CaptureStates.DraftReady, null, ct).ConfigureAwait(false);
            return SendOutcome.Done(sessionId);
        }

        var reason = answer?.Reason ?? $"The backend answered {(int)response.StatusCode}.";
        return response.StatusCode switch
        {
            // The bundle is wrong, and it will be just as wrong in an hour.
            HttpStatusCode.BadRequest => SendOutcome.Failed(reason),

            // This device may not draft. An administrator revoked it, or the token expired; either way
            // sending the same bundle again with the same token is asking the same question.
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SendOutcome.Failed(reason),

            // The budget comes back at midnight; the model comes back on its own; a deployment gets a
            // provider configured. All three are worth asking about again.
            _ => SendOutcome.Retry(reason),
        };
    }

    /// <summary>Reads the answer, and treats one that cannot be read as no answer rather than as a crash.</summary>
    private static async Task<DraftAnswer?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<DraftAnswer>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A proxy's error page, or a backend answering something this version does not understand.
            // The status code still says what to do about it.
            return null;
        }
    }

    /// <summary>
    /// The bundle as the backend reads it: the same field names, with each frame's image in place of its
    /// path.
    ///
    /// Built by hand rather than shared as a type. The backend must not depend on a Windows-only client,
    /// so what binds the two is the JSON, and the field names are the contract — which is why they are
    /// written out here where a change to either side has to be made deliberately.
    /// </summary>
    private async Task<WireBundle> WireAsync(SessionBundle bundle, CancellationToken ct)
    {
        var frames = new List<WireFrame>(bundle.Frames.Count);
        foreach (var frame in bundle.Frames)
        {
            var image = await store.GetRedactedFrameImageAsync(frame.Id, ct).ConfigureAwait(false);
            if (image is null or { Length: 0 })
            {
                // The store will not give back a frame that is still pending, and one purged between
                // selection and now is simply gone. Either way the note is written from the frames that
                // are there rather than refused for the one that is not (INV-1).
                continue;
            }

            frames.Add(new WireFrame(frame.Id, frame.TsMs, frame.OcrText, Convert.ToBase64String(image)));
        }

        return new WireBundle(
            bundle.SessionId,
            bundle.DurationMs,
            bundle.PartialCapture,
            bundle.FramesPurgedUnredacted,
            bundle.OcrPartial,
            frames,
            [.. bundle.Transcript.Select(segment => new WireSegment(segment.Id, segment.TsMs, segment.Text))]);
    }

    private sealed record WireBundle(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("duration_ms")] long? DurationMs,
        [property: JsonPropertyName("partial_capture")] bool PartialCapture,
        [property: JsonPropertyName("frames_purged_unredacted")] long FramesPurgedUnredacted,
        [property: JsonPropertyName("ocr_partial")] bool OcrPartial,
        [property: JsonPropertyName("frames")] IReadOnlyList<WireFrame> Frames,
        [property: JsonPropertyName("transcript")] IReadOnlyList<WireSegment> Transcript);

    private sealed record WireFrame(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ts_ms")] long TsMs,
        [property: JsonPropertyName("ocr_text")] string? OcrText,
        [property: JsonPropertyName("image")] string Image);

    private sealed record WireSegment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ts_ms")] long TsMs,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>What the backend answers. Its <c>status</c> is for a person; the status code decides.</summary>
    private sealed record DraftAnswer(string? Status, string? Reason, DraftNote? Draft);
}
