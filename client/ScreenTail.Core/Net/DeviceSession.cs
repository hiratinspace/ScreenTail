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
    private const string ErasedRefusal = "Everything on this device is being deleted, so it was not activated.";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IDeviceCredentials _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _access;
    private DateTimeOffset _accessExpires;
    private volatile bool _erased;

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
                if (_erased)
                {
                    return null;
                }

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

        if (_erased)
        {
            return GatewayAnswer.Refused<ActivatedDeviceRow>(ErasedRefusal);
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
            if (!Keep(new DeviceCredential(answer.RefreshToken, answer.TenantName ?? string.Empty, answer.DeviceId, now), answer.AccessToken, answer.ExpiresAt))
            {
                return GatewayAnswer.Refused<ActivatedDeviceRow>(ErasedRefusal);
            }

            Revoked = false;
            return GatewayAnswer.Of(new ActivatedDeviceRow(answer.TenantName ?? string.Empty, answer.DeviceId));
        }
    }

    /// <summary>Buys a new access token. False when there was nothing to buy it with, or the backend said no or nothing.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (_erased || _credentials.Load() is not { } credential || _http.BaseAddress is null)
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

            if (!Keep(credential with { LastContactAt = _time.GetUtcNow() }, answer.AccessToken, answer.ExpiresAt))
            {
                return false;
            }

            Revoked = false;
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

    /// <summary>
    /// "Delete everything" (INV-12): the refresh token goes from the store that keeps it, the access
    /// token from memory, and nothing this session does afterwards sends either or saves one back.
    ///
    /// The renewal loop and an activation run on their own. One that was waiting on the backend when
    /// the technician confirmed would otherwise write the refresh token again after it had been removed,
    /// and the device would start its next run still enrolled. The flag and the saves share the gate,
    /// so a save is either finished before this clears it or refused after.
    /// </summary>
    public void Erase()
    {
        lock (_gate)
        {
            _erased = true;
            _access = null;
            _accessExpires = default;
            try
            {
                _credentials.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open. The file is in the data directory the eraser sweeps, and the erasure stays
                // owed on disk until it is gone; this session has already stopped using it.
            }
        }
    }

    /// <summary>Holds the access token and saves the credential, unless everything was erased meanwhile.</summary>
    private bool Keep(DeviceCredential credential, string access, DateTimeOffset expires)
    {
        lock (_gate)
        {
            if (_erased)
            {
                return false;
            }

            // The token first, as a renewal always did: a save that throws (antivirus holding the file)
            // still fails the call, but does not cost the token the backend has just issued.
            _access = access;
            _accessExpires = expires;
            _credentials.Save(credential);
            return true;
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

    /// <summary>What the Settings card says when the backend refused activation for too many tries from this network.</summary>
    public const string TooManyAttempts = "Too many activation attempts from this network. Wait a minute and try again.";

    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // The backend's rate limit answers with no body (ADR-0009): a sentence a technician can act on,
        // rather than a status code.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return TooManyAttempts;
        }

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
