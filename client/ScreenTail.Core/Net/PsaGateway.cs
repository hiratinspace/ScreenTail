using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>The service's way to the backend for publishing (ST-093). The UI never has one.</summary>
public interface IPsaGateway
{
    Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default);

    Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default);

    Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default);
}

/// <summary>
/// Three calls to the backend, through the same guarded client the draft sender uses, with the device
/// token, under the egress guard's user-initiated purpose: a technician pressing Publish is the one thing
/// local-only mode does not stop (INV-8), and the search and the integrations list are that button's
/// preparation. Every refusal is the backend's message, or a sentence of ours when it did not answer.
/// </summary>
public sealed class PsaGateway(HttpClient http, Func<string?> token) : IPsaGateway
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
                request.Content = JsonContent.Create(bundle, options: Json);
                return request;
            },
            answer => [.. answer.Results.Select(r => new PublishOutcomeRow(r.Destination, r.Ok, r.Id, r.Link, r.Error, r.Kind, r.Retryable))],
            ct);
    }

    private async Task<GatewayAnswer<TOut>> AskAsync<TAnswer, TOut>(Func<HttpRequestMessage> build, Func<TAnswer, TOut> map, CancellationToken ct)
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
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
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
            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    var answer = await response.Content.ReadFromJsonAsync<TAnswer>(Json, ct).ConfigureAwait(false);
                    return answer is null
                        ? GatewayAnswer.Refused<TOut>("The backend answered with nothing.")
                        : GatewayAnswer.Of<TOut>(map(answer));
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return GatewayAnswer.Refused<TOut>("The backend answered something this version does not understand.");
                }
            }

            return GatewayAnswer.Refused<TOut>(await RefusalAsync(response, ct).ConfigureAwait(false));
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

    private sealed record IntegrationRowAnswer(string Provider, string? SiteUrl, string? Secret);

    private sealed record TicketsAnswer(IReadOnlyList<TicketRowAnswer> Tickets);

    private sealed record TicketRowAnswer(string Id, string Summary, string Company, string? Status);

    private sealed record PublishAnswer(IReadOnlyList<PublishRowAnswer> Results);

    private sealed record PublishRowAnswer(string Destination, bool Ok, string? Id, string? Link, string? Error, string? Kind, bool Retryable);
}
