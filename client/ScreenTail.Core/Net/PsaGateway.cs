using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Audit;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Net;

/// <summary>The backend's answer: the thing, or its own words about why not. Never a status code the UI has to translate.</summary>
public sealed record GatewayAnswer<T>(T? Value, string? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class GatewayAnswer
{
    public static GatewayAnswer<T> Of<T>(T value) => new(value, null);

    public static GatewayAnswer<T> Refused<T>(string reason) => new(default, reason);
}

/// <summary>One included screenshot on the wire, base64, redacted on this device (INV-1).</summary>
public sealed record PublishWireFrame(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("media_type")] string MediaType = "image/jpeg");

/// <summary>What the service posts to <c>POST /v1/sessions/publish</c>: <c>shared/contracts/publish-request.v1.json</c>.</summary>
public sealed record PublishWire
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("ticket_id")]
    public required string TicketId { get; init; }

    /// <summary>The PSA's name for the ticket's company; the backend maps it to the documentation platform's (ST-097).</summary>
    [JsonPropertyName("company")]
    public string? Company { get; init; }

    [JsonPropertyName("note_type")]
    public required string NoteType { get; init; }

    [JsonPropertyName("minutes")]
    public required int Minutes { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("billable")]
    public bool Billable { get; init; } = true;

    /// <summary>Null lets the backend name the technician the token belongs to.</summary>
    [JsonPropertyName("reviewer")]
    public string? Reviewer { get; init; }

    [JsonPropertyName("footer")]
    public bool Footer { get; init; } = true;

    [JsonPropertyName("destinations")]
    public required IReadOnlyList<string> Destinations { get; init; }

    [JsonPropertyName("note")]
    public required DraftNote Note { get; init; }

    [JsonPropertyName("frames")]
    public required IReadOnlyList<PublishWireFrame> Frames { get; init; }
}

/// <summary><c>GET /v1/integrations/hudu/companies</c>: the documentation platform's companies, and this tenant's mappings so far (ST-097).</summary>
public sealed record CompanyMappingsAnswer(IReadOnlyList<CompanyChoiceRow> Companies, IReadOnlyList<CompanyMappingRow> Mappings);

/// <summary><c>POST /v1/integrations/{provider}/check</c>'s answer: in words either way (ST-082).</summary>
public sealed record IntegrationCheckRow(bool Ok, string Message);

/// <summary>The service's way to the backend for publishing (ST-093) and for Settings → Integrations (ST-082). The UI never has one.</summary>
public interface IPsaGateway
{
    Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default);

    Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default);

    Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default);

    Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default);

    /// <summary>Maps the PSA's company name to a platform company, by hand; the backend remembers it (ST-097 AC2).</summary>
    Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default);

    /// <summary>The same list as <see cref="IntegrationsAsync"/>, with when each was checked and what went wrong.</summary>
    Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default);

    Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default);

    Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default);

    Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default);

    Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default);
}

/// <summary>
/// Three calls to the backend, through the same guarded client the draft sender uses, with the device
/// token, under the egress guard's user-initiated purpose: a technician pressing Publish is the one thing
/// local-only mode does not stop (INV-8), and the search and the integrations list are that button's
/// preparation. Every refusal is the backend's message, or a sentence of ours when it did not answer.
/// </summary>
/// <param name="audit">
/// Where a publish the backend answered is written down as a <c>bundle_sent</c> row: the host and the
/// bytes, never the note (<see cref="SentAudit"/>). Null writes nothing. The other calls send no session.
/// </param>
/// <param name="logger">Where a failed audit write is reported, by exception type only. Null reports nothing.</param>
public sealed class PsaGateway(HttpClient http, Func<string?> token, IAuditLog? audit = null, ILogger? logger = null) : IPsaGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) =>
        AskAsync<IntegrationsAnswer, IReadOnlyList<IntegrationInfo>>(
            () => EgressRequest.For(HttpMethod.Get, new Uri("v1/integrations", UriKind.Relative), EgressPurpose.Publish),
            answer => [.. answer.Integrations.Select(i => new IntegrationInfo(i.Provider, i.SiteUrl ?? string.Empty, i.Secret ?? string.Empty))],
            ct);

    public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
        AskAsync<TicketsAnswer, IReadOnlyList<TicketRow>>(
            () => EgressRequest.For(HttpMethod.Get, new Uri("v1/psa/tickets?q=" + Uri.EscapeDataString(query ?? string.Empty), UriKind.Relative), EgressPurpose.Publish),
            answer => [.. answer.Tickets.Select(t => new TicketRow(t.Id, t.Summary, t.Company, t.Status))],
            ct);

    public Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return AskAsync<PublishAnswer, IReadOnlyList<PublishOutcomeRow>>(
            () =>
            {
                var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/sessions/publish", UriKind.Relative), EgressPurpose.Publish);

                // Serialised here rather than streamed by JsonContent, so the length is known and the audit
                // row says how many bytes left.
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(bundle, Json));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return request;
            },
            answer => [.. answer.Results.Select(r => new PublishOutcomeRow(r.Destination, r.Ok, r.Id, r.Link, r.Error, r.Kind, r.Retryable))],
            ct,
            sentFor: bundle.SessionId,
            mayHaveLeft: cause => GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>([.. bundle.Destinations.Select(d => MayHaveLanded(d, cause))]));
    }

    /// <summary>
    /// O4 (owner, review of #265): a publish that failed once the request may have left the machine. The
    /// backend may have posted it, so each destination is <c>unknown</c>, not retryable, and says to check
    /// first, in the backend's own words for its <c>unknown</c> rows.
    /// </summary>
    private static PublishOutcomeRow MayHaveLanded(string destination, string cause) => new(
        destination,
        false,
        Error: destination switch
        {
            "ticket_note" => $"{cause}, so the note may already be on the ticket. Check the ticket before you publish the note again.",
            "time_entry" => $"{cause}, so the time entry may already be on the ticket. Check the ticket before you publish the time entry again.",
            _ => $"{cause}, so the article may already be written. Check the knowledge base before you publish the article again.",
        },
        Kind: "unknown",
        Retryable: false);

    /// <summary>
    /// Whether an <see cref="HttpRequestException"/> means the request never reached the backend. Only
    /// three of .NET 10's <see cref="HttpRequestError"/> values say so, in the reference documentation
    /// (System.Net.Http.xml, Microsoft.NETCore.App.Ref 10.0): <c>NameResolutionError</c> "The DNS name
    /// resolution failed", <c>ConnectionError</c> "A transport-level failure occurred while connecting to
    /// the remote endpoint", and <c>SecureConnectionError</c> "An error occurred during the TLS handshake".
    /// Every other value, <c>Unknown</c> ("A generic or unknown error occurred") and <c>ResponseEnded</c>
    /// ("The response ended prematurely") included, may come after the body went, so it is not one of these.
    /// <para>
    /// Checked against the .NET 10 source (dotnet/runtime, branch release/10.0, under
    /// src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/), owner decisions 5 and 6 on #265:
    /// </para>
    /// <para>
    /// ConnectionError is raised in one place, ConnectHelper.cs:162 (CreateWrappedException's DeduceError),
    /// reached from the TCP connect in ConnectionPool/HttpConnectionPool.cs:706 (ConnectToTcpHostAsync), the
    /// QUIC connect at ConnectHelper.cs:135, and ConnectionPool/HttpConnectionPool.Http3.cs:273 (no HTTP/3
    /// by ALPN). All of them come before a connection exists to write a request on, so it never follows a
    /// written body. An IOException on an open connection maps to the error its HttpIOException carries, or
    /// Unknown (HttpConnection.cs:934), never ConnectionError.
    /// </para>
    /// <para>
    /// The pool retries only a failure marked RetryOnConnectionFailure (ConnectionPool/HttpConnectionPool.cs:498).
    /// On HTTP/1.1 that mark comes from HttpConnection.cs:935 when _canRetry is set, which is cleared as a
    /// request starts (HttpConnection.cs:546) and set at HttpConnection.cs:657 only when the server closed
    /// the connection without a byte of answer and the request has no body, or an Expect: 100-continue body
    /// not yet released; HttpConnection.cs:853 clears it again if that body was released. A publish is a POST
    /// with a body and no Expect header on HTTP/1.1 (HttpClient's default version), so it is never retried.
    /// </para>
    /// </summary>
    private static bool NeverSent(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;

    public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) =>
        AskAsync<MappingsAnswer, CompanyMappingsAnswer>(
            () => EgressRequest.For(HttpMethod.Get, new Uri("v1/integrations/hudu/companies", UriKind.Relative), EgressPurpose.Publish),
            answer => new CompanyMappingsAnswer(
                [.. answer.Companies.Select(c => new CompanyChoiceRow(c.Id, c.Name))],
                [.. answer.Mappings.Select(m => new CompanyMappingRow(m.PsaCompany, m.DocCompanyId, m.DocCompanyName, m.Confidence))]),
            ct);

    public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
        SendAsync(
            () =>
            {
                var request = EgressRequest.For(HttpMethod.Put, new Uri("v1/integrations/hudu/companies", UriKind.Relative), EgressPurpose.Publish);
                request.Content = JsonContent.Create(new MapCompanyBody(psaCompany, docCompanyId), options: Json);
                return request;
            },
            (response, _) => Task.FromResult(response.IsSuccessStatusCode ? GatewayAnswer.Of(true) : null),
            ct);

    public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) =>
        AskAsync<IntegrationsAnswer, IReadOnlyList<IntegrationDetailRow>>(
            () => EgressRequest.For(HttpMethod.Get, new Uri("v1/integrations", UriKind.Relative), EgressPurpose.Publish),
            answer => [.. answer.Integrations.Select(i => new IntegrationDetailRow(i.Provider, i.SiteUrl ?? string.Empty, i.Secret ?? string.Empty, i.ConnectedAt, i.LastCheckedAt, i.LastError))],
            ct);

    public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
        SendAsync(
            () =>
            {
                var request = EgressRequest.For(HttpMethod.Put, new Uri("v1/integrations/" + Uri.EscapeDataString(provider ?? string.Empty), UriKind.Relative), EgressPurpose.Publish);
                request.Content = JsonContent.Create(new StoreIntegrationBody(siteUrl, secret), options: Json);
                return request;
            },
            (response, _) => Task.FromResult(response.IsSuccessStatusCode ? GatewayAnswer.Of(true) : null),
            ct);

    public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default) =>
        SendAsync(
            () => EgressRequest.For(HttpMethod.Delete, new Uri("v1/integrations/" + Uri.EscapeDataString(provider ?? string.Empty), UriKind.Relative), EgressPurpose.Publish),
            (response, _) => Task.FromResult(response.IsSuccessStatusCode ? GatewayAnswer.Of(true) : null),
            ct);

    public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) =>
        AskAsync<CheckAnswer, IntegrationCheckRow>(
            () => EgressRequest.For(HttpMethod.Post, new Uri("v1/integrations/" + Uri.EscapeDataString(provider ?? string.Empty) + "/check", UriKind.Relative), EgressPurpose.Publish),
            answer => new IntegrationCheckRow(answer.Ok, answer.Message ?? string.Empty),
            ct);

    public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default) =>
        SendAsync(
            () => EgressRequest.For(HttpMethod.Delete, new Uri("v1/integrations/hudu/companies/" + Uri.EscapeDataString(psaCompany ?? string.Empty), UriKind.Relative), EgressPurpose.Publish),
            (response, _) => Task.FromResult(response.IsSuccessStatusCode ? GatewayAnswer.Of(true) : null),
            ct);

    /// <param name="mayHaveLeft">
    /// For a call that acts (a publish): the answer to give when it failed after the request may have left,
    /// from a sentence saying why. Null keeps every failure a plain refusal, which is right for a question.
    /// </param>
    private Task<GatewayAnswer<TOut>> AskAsync<TAnswer, TOut>(Func<HttpRequestMessage> build, Func<TAnswer, TOut> map, CancellationToken ct, string? sentFor = null, Func<string, GatewayAnswer<TOut>>? mayHaveLeft = null) =>
        SendAsync(
            build,
            async (response, token) =>
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return null;
                }

                try
                {
                    var answer = await response.Content.ReadFromJsonAsync<TAnswer>(Json, token).ConfigureAwait(false);
                    return answer is null
                        ? Unread(mayHaveLeft, "The backend answered with nothing.")
                        : GatewayAnswer.Of<TOut>(map(answer));
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return Unread(mayHaveLeft, "The backend answered something this version does not understand.");
                }
                catch (Exception ex) when (mayHaveLeft is not null && ex is HttpRequestException or IOException)
                {
                    // The body broke off: the backend had accepted the request and was answering it. Not
                    // reached while HttpClient reads the whole body before SendAsync returns (its default,
                    // ResponseContentRead): the break then comes out of SendAsync as an HttpRequestException
                    // and is unknown there (APublishWhoseAnswerBrokeOffIsUnknown). Kept for a caller that
                    // asks for headers first.
                    return mayHaveLeft("The backend's answer broke off");
                }
            },
            ct,
            sentFor,
            mayHaveLeft);

    /// <summary>An accepted request whose answer could not be read: a refusal for a question, unknown for a publish.</summary>
    private static GatewayAnswer<TOut> Unread<TOut>(Func<string, GatewayAnswer<TOut>>? mayHaveLeft, string refusal) =>
        mayHaveLeft is null ? GatewayAnswer.Refused<TOut>(refusal) : mayHaveLeft("The backend's answer could not be read");

    /// <summary>
    /// The guarded send every call shares. <paramref name="read"/> turns a response it accepts into the
    /// answer and returns null for one it does not, which is then read as the backend's refusal.
    /// </summary>
    /// <param name="sentFor">
    /// The session whose bytes this request carries, for its <c>bundle_sent</c> row; null for a call that
    /// carries none. The row is written once the answer has been read, whatever the answer was: a refused
    /// publish left the machine as surely as an accepted one. Also when no answer came and the request may
    /// have left all the same (a reset, a timeout); not when the connection never opened.
    /// </param>
    /// <param name="mayHaveLeft">See <see cref="AskAsync{TAnswer, TOut}"/>. When in doubt, it is used: a needless check of the ticket is cheap, a second note on it is not.</param>
    private async Task<GatewayAnswer<TOut>> SendAsync<TOut>(Func<HttpRequestMessage> build, Func<HttpResponseMessage, CancellationToken, Task<GatewayAnswer<TOut>?>> read, CancellationToken ct, string? sentFor = null, Func<string, GatewayAnswer<TOut>>? mayHaveLeft = null)
    {
        if (http.BaseAddress is null)
        {
            return GatewayAnswer.Refused<TOut>("No backend is configured, so nothing can be published.");
        }

        if (token() is not { Length: > 0 } bearer)
        {
            return GatewayAnswer.Refused<TOut>("This device is not enrolled yet, so nothing can be published.");
        }

        using var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var bytes = sentFor is null ? null : request.Content?.Headers.ContentLength;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (mayHaveLeft is not null && !NeverSent(ex))
        {
            // The bytes may have left, so they are recorded as sent (m-h): the same row an answered
            // publish writes, a session id, a byte count and a host, no content (INV-10).
            await RecordSentAsync(sentFor, bytes, request).ConfigureAwait(false);
            return mayHaveLeft("The backend did not answer");
        }
        catch (TaskCanceledException) when (mayHaveLeft is not null && !ct.IsCancellationRequested)
        {
            // HttpClient.Timeout: the request was on its way, and may have been acted on, when it fired.
            await RecordSentAsync(sentFor, bytes, request).ConfigureAwait(false);
            return mayHaveLeft("The backend did not answer in time");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return GatewayAnswer.Refused<TOut>("The backend did not answer.");
        }
        catch (EgressBlockedException blocked)
        {
            return GatewayAnswer.Refused<TOut>(blocked.Message);
        }

        using (response)
        {
            try
            {
                // The answer first: it is what the technician is waiting for, and an audit row that cannot
                // be written must not cost it.
                if (mayHaveLeft is not null && (int)response.StatusCode >= 500)
                {
                    // A 5xx may come from the backend partway through, or from a proxy after it: either way
                    // the request reached something that may have passed it on. A 4xx is the backend's
                    // refusal before it acted, and stays a plain one below.
                    return await NothingConnectedAsync(response, ct).ConfigureAwait(false) is { } nothing
                        ? GatewayAnswer.Refused<TOut>(nothing)
                        : mayHaveLeft($"The backend answered {(int)response.StatusCode}");
                }

                return await read(response, ct).ConfigureAwait(false)
                    ?? (mayHaveLeft is not null && response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.OK
                        ? mayHaveLeft($"The backend answered {(int)response.StatusCode}")
                        : GatewayAnswer.Refused<TOut>(await RefusalAsync(response, ct).ConfigureAwait(false)));
            }
            finally
            {
                if (sentFor is not null)
                {
                    await SentAudit.RecordAsync(audit, sentFor, bytes, response.RequestMessage?.RequestUri ?? request.RequestUri, http.BaseAddress, logger).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The <c>bundle_sent</c> row for a request with no answer that may have left all the same.</summary>
    private Task RecordSentAsync(string? sentFor, long? bytes, HttpRequestMessage request) =>
        sentFor is null ? Task.CompletedTask : SentAudit.RecordAsync(audit, sentFor, bytes, request.RequestUri, http.BaseAddress, logger);

    /// <summary>
    /// The backend's own 501 for a tenant with nothing connected (<c>no_psa</c>, <c>no_docs</c>,
    /// <c>not_configured</c>), which it answers before doing anything: its sentence, or null for any other
    /// 5xx, a proxy's 501 included. Read once, so nothing reads the body after it.
    /// </summary>
    private static async Task<string?> NothingConnectedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.NotImplemented)
        {
            return null;
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, ct).ConfigureAwait(false);
            return problem is { Error: "no_psa" or "no_docs" or "not_configured" }
                ? problem.Message is { Length: > 0 } message ? message : "Connect a PSA to publish."
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>The backend's own sentence when it sent one; the status in words when it did not.</summary>
    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, ct).ConfigureAwait(false);
            if (problem?.Message is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A proxy's page, or a shape this version does not know. The status still says enough.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "This device may not publish. Its enrolment was revoked or its token expired.",
            HttpStatusCode.NotImplemented => "Connect a PSA to publish.",
            _ => $"The backend answered {(int)response.StatusCode}.",
        };
    }

    private sealed record Problem(string? Error, string? Message);

    private sealed record IntegrationsAnswer(IReadOnlyList<IntegrationRowAnswer> Integrations);

    private sealed record IntegrationRowAnswer(string Provider, string? SiteUrl, string? Secret, DateTimeOffset? ConnectedAt = null, DateTimeOffset? LastCheckedAt = null, string? LastError = null);

    private sealed record StoreIntegrationBody(string SiteUrl, string Secret);

    private sealed record CheckAnswer(bool Ok, string? Message);

    private sealed record TicketsAnswer(IReadOnlyList<TicketRowAnswer> Tickets);

    private sealed record TicketRowAnswer(string Id, string Summary, string Company, string? Status);

    private sealed record PublishAnswer(IReadOnlyList<PublishRowAnswer> Results);

    private sealed record PublishRowAnswer(string Destination, bool Ok, string? Id, string? Link, string? Error, string? Kind, bool Retryable);

    private sealed record MappingsAnswer(IReadOnlyList<CompanyAnswer> Companies, IReadOnlyList<MappingRowAnswer> Mappings);

    private sealed record CompanyAnswer(string Id, string Name);

    private sealed record MappingRowAnswer(string PsaCompany, string DocCompanyId, string DocCompanyName, string Confidence);

    private sealed record MapCompanyBody(string PsaCompany, string DocCompanyId);
}
