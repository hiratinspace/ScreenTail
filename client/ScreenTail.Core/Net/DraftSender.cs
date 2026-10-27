using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Audit;
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
/// <param name="logger">Where a failed audit write is reported, by exception type only. Null reports nothing.</param>
public sealed class DraftSender(
    HttpClient http,
    ISessionStore store,
    Func<string?> token,
    BundleOptions? options = null,
    Func<IReadOnlyList<string>>? styleHints = null,
    IAuditLog? audit = null,
    TimeProvider? time = null,
    ILogger? logger = null)
{
    /// <summary>The audit detail when a host cannot be written as a label: still a row, still the bytes.</summary>
    public const string UnnamedHost = "unnamed-host";

    private const long NoHold = long.MinValue;

    private readonly IAuditLog? _audit = audit ?? store as IAuditLog;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Where the backend drafts. Relative, so the host's base address decides the deployment.</summary>
    private static readonly Uri Endpoint = new("v1/sessions/summarize", UriKind.Relative);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long drafting is held after the backend says no provider is configured and names no time.
    /// An operator fixes that in hours rather than seconds, and this is the outbox's own longest wait,
    /// so nothing is parked longer than it would have been anyway.
    /// </summary>
    public static readonly TimeSpan NotConfiguredHold = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a draft waits while the device is not enrolled. Activation can be skipped and done later
    /// from Settings, so the draft goes within minutes of it rather than on the outbox's hourly cap.
    /// </summary>
    public static readonly TimeSpan NotEnrolledWait = TimeSpan.FromMinutes(5);

    // "Drafting is blocked until": set by a 402 or a 501, read before anything else on every send. One
    // sender serves every queued draft, so the hold is process-wide, which is the point: after the
    // budget is spent every other session's retry used to rebuild and upload its ~6 MB bundle for the
    // backend to refuse after reading it (2026-10-12 review, E-11). Its end is a monotonic timestamp, so
    // a wall clock set back cannot lengthen it, under Interlocked because nothing here should depend on
    // the outbox draining on one thread.
    private long _holdEnds = NoHold;
    private volatile string? _holdReason;

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

        // Before the token and before the store. A hold means the backend has already said, for every
        // session on this device, that it will refuse; nothing is built for it to refuse again. Neither
        // this nor an unenrolled device is an attempt, and the outbox is told so: patience is for sends.
        if (HeldFor() is { } remaining)
        {
            return SendOutcome.NotSent(_holdReason ?? "The backend is not drafting at the moment.", remaining);
        }

        if (token() is not { Length: > 0 } bearer)
        {
            return SendOutcome.NotSent("This device is not enrolled yet, so the session was not sent.", NotEnrolledWait);
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

        // Serialised here rather than by JsonContent, so the audit row below says how many bytes left
        // rather than what a length header happened to claim.
        var body = JsonSerializer.SerializeToUtf8Bytes(wire, Json);
        using var request = EgressRequest.For(HttpMethod.Post, Endpoint, EgressPurpose.Summarisation);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

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
            try
            {
                // The draft first. Once the backend answered, the model has been paid for, and the note
                // is kept before anything else is attempted.
                return await InterpretAsync(response, item.SessionId, ct).ConfigureAwait(false);
            }
            finally
            {
                // An answer came back, so the bytes left, whatever the answer was: a refused bundle
                // crossed the network as surely as an accepted one. This is the row the audit export's
                // "bytes sent, to which host" section is made of (2026-10-12 review, S-15). The host and
                // the count, never the body (INV-10). A request that threw above is not recorded: whether
                // its bytes left is unknown, and the audit log does not guess.
                // The same row, written the same way, as the publish call and the metric (SentAudit): never
                // costing the draft, not cancellable once the bytes have left, a failure logged by type.
                await SentAudit.RecordAsync(_audit, item.SessionId, body.Length, response.RequestMessage?.RequestUri ?? request.RequestUri, http.BaseAddress, logger).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How long drafting is still held, or null when it is not.</summary>
    private TimeSpan? HeldFor()
    {
        var ends = Interlocked.Read(ref _holdEnds);
        if (ends == NoHold)
        {
            return null;
        }

        var remaining = _time.GetElapsedTime(_time.GetTimestamp(), ends);
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        return remaining > Outbox.Outbox.MaxRetryAfter ? Outbox.Outbox.MaxRetryAfter : remaining;
    }

    /// <summary>
    /// Holds every send until <paramref name="until"/>. The reason travels with the holds that follow,
    /// so the outbox's last error still says why rather than only that it waited.
    /// </summary>
    /// <returns>How long the hold lasts.</returns>
    private TimeSpan Hold(TimeSpan wait, string reason)
    {
        // Bounded like any far end's Retry-After: one header naming a month must not stop drafting on
        // this device for a month, and nothing waits longer than the outbox would honour anyway.
        if (wait > Outbox.Outbox.MaxRetryAfter)
        {
            wait = Outbox.Outbox.MaxRetryAfter;
        }

        var ends = _time.GetTimestamp() + (long)((Int128)wait.Ticks * _time.TimestampFrequency / TimeSpan.TicksPerSecond);
        _holdReason = reason;
        _ = Interlocked.Exchange(ref _holdEnds, ends);
        return wait;
    }

    /// <summary>The next UTC midnight: the backend's cost ledger counts the day in UTC, and that is when a spent budget reopens.</summary>
    private static DateTimeOffset NextUtcMidnight(DateTimeOffset now) => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private async Task<SendOutcome> InterpretAsync(HttpResponseMessage response, string sessionId, CancellationToken ct)
    {
        var answer = await ReadAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK
            && answer is not null
            && (answer.Draft is { ValueKind: not JsonValueKind.Null } || answer.Status == "ok"))
        {
            // The backend's own answer, and it drafted: the model has been paid. A draft this version
            // cannot read is not asked for again, because asking buys the same answer at the same price,
            // and that loop is what a renamed field used to cost (2026-10-12 review). The technician is
            // told, and can retry by hand once the device is updated.
            if (Draft(answer.Draft) is not { } draft)
            {
                return SendOutcome.Failed("The backend drafted this session, but this version of ScreenTail could not read the draft.");
            }

            await store.SaveDraftAsync(sessionId, draft, ct).ConfigureAwait(false);
            await store.SetSessionStateAsync(sessionId, CaptureStates.DraftReady, null, ct).ConfigureAwait(false);
            return SendOutcome.Done(sessionId);
        }

        var reason = answer?.Reason ?? $"The backend answered {(int)response.StatusCode}.";
        var now = _time.GetUtcNow();
        // A wait of nothing, or a date already past, would be no hold at all, so it counts as not named.
        var named = response.Headers.RetryAfter is { } after
            ? after.Delta ?? (after.Date is { } date ? date - now : null)
            : null;
        if (named <= TimeSpan.Zero)
        {
            named = null;
        }

        switch (response.StatusCode)
        {
            // The tenant's budget is spent for the day. It reopens at UTC midnight, or when the backend
            // says if it says; until then every draft on this device would be refused the same way, so
            // none is built (E-11, paired with E-13's bound on the outbox). The refusal already says how
            // long, so the outbox does not spend an attempt finding out.
            case HttpStatusCode.PaymentRequired:
                return SendOutcome.Retry(reason, Hold(named ?? NextUtcMidnight(now) - now, reason));

            // No provider is configured. A deployment problem an operator fixes in hours.
            case HttpStatusCode.NotImplemented:
                return SendOutcome.Retry(reason, Hold(named ?? NotConfiguredHold, reason));
        }

        return response.StatusCode switch
        {
            // The bundle is wrong, and it will be just as wrong in an hour.
            HttpStatusCode.BadRequest => SendOutcome.Failed(reason),

            // This device may not draft. An administrator revoked it, or the token expired; either way
            // sending the same bundle again with the same token is asking the same question.
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SendOutcome.Failed(reason),

            // The model answered twice and neither answer could be shown. Asking again costs a model call
            // for the same answer, and this was retried until the outbox gave up (2026-10-12 review).
            HttpStatusCode.UnprocessableEntity => SendOutcome.Failed(reason),

            // The model is rate limited or down. The provider may have said when it reopens, and under a
            // daily quota that is hours: the outbox waits at least that long rather than spending its
            // attempts inside the hour.
            HttpStatusCode.ServiceUnavailable => SendOutcome.Retry(reason, response.Headers.RetryAfter?.Delta),

            // The budget comes back at midnight; this device's other drafts finish; a deployment gets a
            // provider configured. All of them are worth asking about again.
            _ => SendOutcome.Retry(reason),
        };
    }

    /// <summary>The draft out of the answer, or null when this version cannot read it.</summary>
    private static DraftNote? Draft(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } draft)
        {
            return null;
        }

        try
        {
            return draft.Deserialize<DraftNote>(Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
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
            [.. bundle.Transcript.Select(segment => new WireSegment(segment.Id, segment.TsMs, segment.Text))],
            styleHints?.Invoke() ?? []);
    }

    private sealed record WireBundle(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("duration_ms")] long? DurationMs,
        [property: JsonPropertyName("partial_capture")] bool PartialCapture,
        [property: JsonPropertyName("frames_purged_unredacted")] long FramesPurgedUnredacted,
        [property: JsonPropertyName("ocr_partial")] bool OcrPartial,
        [property: JsonPropertyName("frames")] IReadOnlyList<WireFrame> Frames,
        [property: JsonPropertyName("transcript")] IReadOnlyList<WireSegment> Transcript,
        [property: JsonPropertyName("style_hints")] IReadOnlyList<string> StyleHints);

    private sealed record WireFrame(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ts_ms")] long TsMs,
        [property: JsonPropertyName("ocr_text")] string? OcrText,
        [property: JsonPropertyName("image")] string Image);

    private sealed record WireSegment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ts_ms")] long TsMs,
        [property: JsonPropertyName("text")] string Text);

    /// <summary>
    /// What the backend answers, as <c>shared/contracts/summarize-response.v1.json</c> lists it. Its
    /// <c>status</c> is for a person; the status code decides. The draft is read on its own, so a draft
    /// this version cannot read is told apart from an answer that is not the backend's at all.
    /// </summary>
    private sealed record DraftAnswer(string? Status, string? Reason, JsonElement? Draft);
}
