using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScreenTail.Core.Net;

/// <param name="RefreshToken">Long-lived, shown once by the backend, kept here under DPAPI and never logged.</param>
/// <param name="LastContactAt">The last time the backend answered this device, for the grace count.</param>
public sealed record DeviceCredential(string RefreshToken, string TenantName, Guid DeviceId, DateTimeOffset LastContactAt);

/// <summary>Where the refresh token lives between runs. DPAPI on Windows; memory in tests.</summary>
public interface IDeviceCredentials
{
    DeviceCredential? Load();

    void Save(DeviceCredential credential);

    void Clear();
}

public sealed record ActivatedDeviceRow(string TenantName, Guid DeviceId);

/// <summary>ST-010 AC3: seven days from the last answer. A number the diagnostics show, not a switch capture trips on.</summary>
public static class OfflineGrace
{
    public static readonly TimeSpan Length = TimeSpan.FromDays(7);

    public static bool Beyond(DateTimeOffset lastContact, DateTimeOffset now) => now - lastContact > Length;
}

/// <summary>
/// The device's standing with the backend (ST-010): activated once with an invite's code, then an
/// access token an hour at a time from the refresh token. <see cref="CurrentToken"/> is what every
/// backend call sends; it is null five minutes before the access token runs out, so a request at the
/// boundary is not sent with a token about to be refused, and <see cref="RunAsync"/> renews it before
/// then. Without a stored credential the environment's token is used, which is how the M1 runbook
/// enrols until the client's onboarding exists (ST-083).
///
/// A refusal on refresh means the device was revoked or the tenant switched off; the token goes and
/// <see cref="Standing"/> says so. A backend that does not answer changes nothing except the count of
/// days since it last did.
/// </summary>
public sealed class DeviceSession(HttpClient http, IDeviceCredentials credentials, Func<string?>? fallbackToken = null, TimeProvider? time = null)
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(50);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IDeviceCredentials _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _access;
    private DateTimeOffset _accessExpires;

    public bool Activated => _credentials.Load() is not null;

    public string? TenantName => _credentials.Load()?.TenantName;

    public Guid? DeviceId => _credentials.Load()?.DeviceId;

    public bool Revoked { get; private set; }

    /// <summary>The bearer token for the next backend call, or null when there is none to send.</summary>
    public string? CurrentToken
    {
        get
        {
            lock (_gate)
            {
                if (_access is not null && _time.GetUtcNow() < _accessExpires - Margin)
                {
                    return _access;
                }
            }

            return _credentials.Load() is null ? fallbackToken?.Invoke() : null;
        }
    }

    public int DaysOffline => _credentials.Load() is { } credential
        ? Math.Max(0, (int)(_time.GetUtcNow() - credential.LastContactAt).TotalDays)
        : 0;

    public bool BeyondGrace => _credentials.Load() is { } credential && OfflineGrace.Beyond(credential.LastContactAt, _time.GetUtcNow());

    /// <summary>One sentence for the diagnostics panel and Settings.</summary>
    public string Standing
    {
        get
        {
            if (Revoked)
            {
                return "This device's access was revoked. Ask your admin for a new invite.";
            }

            if (_credentials.Load() is not { } credential)
            {
                return fallbackToken?.Invoke() is { Length: > 0 } ? "Enrolled from the environment (development)." : "Not activated. Enter the code from your invite.";
            }

            if (BeyondGrace)
            {
                return $"The backend has not answered for {DaysOffline} days, past the 7 days of grace. Capture continues; drafts wait.";
            }

            return DaysOffline > 0
                ? $"Activated with {credential.TenantName}. The backend last answered {DaysOffline} day(s) ago."
                : $"Activated with {credential.TenantName}.";
        }
    }

    public async Task<GatewayAnswer<ActivatedDeviceRow>> ActivateAsync(string code, string deviceName, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null)
        {
            return GatewayAnswer.Refused<ActivatedDeviceRow>("No backend is configured, so this device cannot be activated.");
        }

        using var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/devices/activate", UriKind.Relative), EgressPurpose.Publish);
        request.Content = JsonContent.Create(new ActivateBody(code ?? string.Empty, deviceName ?? string.Empty), options: Json);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return GatewayAnswer.Refused<ActivatedDeviceRow>("The backend did not answer.");
        }
        catch (EgressBlockedException blocked)
        {
            return GatewayAnswer.Refused<ActivatedDeviceRow>(blocked.Message);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return GatewayAnswer.Refused<ActivatedDeviceRow>(await RefusalAsync(response, ct).ConfigureAwait(false));
            }

            ActivatedAnswer? answer;
            try
            {
                answer = await response.Content.ReadFromJsonAsync<ActivatedAnswer>(Json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                answer = null;
            }

            if (answer is null || string.IsNullOrEmpty(answer.RefreshToken) || string.IsNullOrEmpty(answer.AccessToken))
            {
                return GatewayAnswer.Refused<ActivatedDeviceRow>("The backend answered something this version does not understand.");
            }

            var now = _time.GetUtcNow();
            _credentials.Save(new DeviceCredential(answer.RefreshToken, answer.TenantName ?? string.Empty, answer.DeviceId, now));
            Remember(answer.AccessToken, answer.ExpiresAt);
            Revoked = false;
            return GatewayAnswer.Of(new ActivatedDeviceRow(answer.TenantName ?? string.Empty, answer.DeviceId));
        }
    }

    /// <summary>Buys a new access token. False when there was nothing to buy it with, or the backend said no or nothing.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (_credentials.Load() is not { } credential || _http.BaseAddress is null)
        {
            return false;
        }

        using var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/devices/token", UriKind.Relative), EgressPurpose.Publish);
        request.Content = JsonContent.Create(new RefreshBody(credential.RefreshToken), options: Json);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested) || ex is EgressBlockedException)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Revoked = true;
                Forget();
                return false;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            TokenAnswer? answer;
            try
            {
                answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(Json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return false;
            }

            if (answer is null || string.IsNullOrEmpty(answer.AccessToken))
            {
                return false;
            }

            Remember(answer.AccessToken, answer.ExpiresAt);
            Revoked = false;
            _credentials.Save(credential with { LastContactAt = _time.GetUtcNow() });
            return true;
        }
    }

    /// <summary>The renewal loop the host runs: every fifty minutes, sooner after a failure.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var renewed = await RefreshAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(renewed ? RefreshEvery : RetryAfterFailure, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Remember(string access, DateTimeOffset expires)
    {
        lock (_gate)
        {
            _access = access;
            _accessExpires = expires;
        }
    }

    private void Forget()
    {
        lock (_gate)
        {
            _access = null;
            _accessExpires = default;
        }
    }

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
            // A proxy's page, or a shape this version does not know.
        }

        return $"The backend answered {(int)response.StatusCode}.";
    }

    private sealed record ActivateBody(string Code, string DeviceName);

    private sealed record RefreshBody(string RefreshToken);

    private sealed record ActivatedAnswer(string? TenantName, Guid DeviceId, string? RefreshToken, string? AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenAnswer(string? AccessToken, DateTimeOffset ExpiresAt);

    private sealed record Problem(string? Error, string? Message);
}
