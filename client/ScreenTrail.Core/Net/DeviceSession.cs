using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScreenTrail.Core.Net;

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

/// <param name="Token">The bearer token to send, or null when there is none.</param>
/// <param name="Refusal">Why there is none, in words a technician can read. Null when there is a token.</param>
public sealed record DeviceSignIn(string? Token, string? Refusal);

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
    private const string ErasedSignIn = "Everything on this device is being deleted, so it did not sign in.";
    private const string StoppingSignIn = "ScreenTrail is stopping, so this device did not sign in.";

    /// <summary>How long a sign-in waits for the backend before saying it did not answer.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(20);

    /// <summary>What a sign-in the backend did not answer says, in place of "not enrolled".</summary>
    public const string NotAnswered = "The backend did not answer, so this device could not sign in. Try again in a moment.";

    /// <summary>What the device card says, and a sign-in the backend refused, once access was revoked.</summary>
    public const string RevokedStanding = "This device's access was revoked. Ask your ScreenTrail owner for a new invite.";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IDeviceCredentials _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _access;
    private DateTimeOffset _accessExpires;
    private volatile bool _erased;
    private Task<string?>? _signingIn;
    private EgressPurpose? _signingInFor;

    /// <summary>
    /// What the device card says, and an activation is refused with, when no backend address is configured
    /// for a reason the technician cannot fix: an installed build whose administrator has not set the
    /// address (<see cref="DeploymentAddresses"/>). Null keeps the plain wording.
    /// </summary>
    public string? NoBackendNote { get; init; }

    /// <summary>
    /// Cancelled when the service stops (the host passes its stopping token). A sign-in in flight then
    /// ends at once, answered as not signed in, rather than holding the stop for up to
    /// <see cref="SignInTimeout"/>; nothing is thrown, so a stop is not logged as a failed renewal.
    /// </summary>
    public CancellationToken Stopping { get; init; }

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
                return RevokedStanding;
            }

            if (_http.BaseAddress is null && NoBackendNote is { } note)
            {
                return note;
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
            return GatewayAnswer.Refused<ActivatedDeviceRow>(NoBackendNote ?? "No backend is configured, so this device cannot be activated.");
        }

        if (_erased)
        {
            return GatewayAnswer.Refused<ActivatedDeviceRow>(ErasedRefusal);
        }

        // The technician typed the code and pressed Activate: a sign-in they asked for, which local-only
        // lets out the way it lets out the publish it is for (INV-8).
        using var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/devices/activate", UriKind.Relative), EgressPurpose.DeviceAuth)
            .OnBehalfOf(EgressPurpose.Publish);
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

    /// <summary>
    /// Buys a new access token on the background schedule. False when there was nothing to buy it with, or
    /// the backend said no or nothing, or local-only mode refused a renewal nobody asked for (INV-8).
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default) =>
        await SharedSignInAsync(onBehalfOf: null, ct).ConfigureAwait(false) is null;

    /// <summary>
    /// Buys a new access token for <paramref name="onBehalfOf"/>: <see cref="EgressPurpose.Publish"/> when the
    /// technician pressed something, <see cref="EgressPurpose.Policy"/> before a policy fetch. Under local-only
    /// those are the only renewals that go (2026-11-15 review).
    /// </summary>
    public async Task<bool> RefreshAsync(EgressPurpose onBehalfOf, CancellationToken ct = default) =>
        await SharedSignInAsync(onBehalfOf, ct).ConfigureAwait(false) is null;

    /// <summary>
    /// The token to send for <paramref name="onBehalfOf"/>, renewed first when the one in hand has run out
    /// or is about to. This is what lets a publish after a long idle under local-only work: the background
    /// renewal does not run there, so the token the publish needs is bought when the publish asks for it.
    /// Null when there is none to send (not activated, revoked, or the backend did not answer).
    /// </summary>
    public async Task<string?> EnsureTokenAsync(EgressPurpose onBehalfOf, CancellationToken ct = default) =>
        (await SignInAsync(onBehalfOf, ct).ConfigureAwait(false)).Token;

    /// <summary>
    /// <see cref="EnsureTokenAsync"/>, with the reason when there is no token: the backend did not answer,
    /// the device was revoked, local-only refused it. A caller that would otherwise say "not enrolled" says
    /// this instead (2026-11-15 review).
    /// </summary>
    public async Task<DeviceSignIn> SignInAsync(EgressPurpose onBehalfOf, CancellationToken ct = default)
    {
        if (CurrentToken is { } current)
        {
            return new DeviceSignIn(current, null);
        }

        string? refusal;
        try
        {
            refusal = await SharedSignInAsync(onBehalfOf, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A credential save that throws comes after the new token is held; the caller still gets it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            refusal = null;
        }

        return CurrentToken is { } token
            ? new DeviceSignIn(token, null)
            : new DeviceSignIn(null, refusal ?? NotAnswered);
    }

    /// <summary>
    /// One sign-in at a time, which a caller arriving meanwhile waits for rather than sending its own: a
    /// publish, the policy fetch and the timer in the same second buy one token, not three. A sign-in on
    /// behalf of something does not wait on a background renewal, which local-only may refuse where it
    /// would not refuse that one.
    ///
    /// The request runs detached from every caller, the one that started it included: each only waits on
    /// it with its own token, so a publish the technician cancels, or the service stopping, stops waiting
    /// at once while the sign-in goes on for whoever else joined it. The request itself is bounded by
    /// <see cref="SignInTimeout"/> and by <see cref="Stopping"/>. One that faulted is finished, so the next
    /// sign-in sends a fresh request rather than sharing the fault.
    /// </summary>
    /// <returns>Null when a new token was bought; otherwise why not, in words a technician can read.</returns>
    private Task<string?> SharedSignInAsync(EgressPurpose? onBehalfOf, CancellationToken ct)
    {
        Task<string?> running;
        lock (_gate)
        {
            if (_signingIn is { IsCompleted: false } inFlight && (onBehalfOf is null || _signingInFor is not null))
            {
                running = inFlight;
            }
            else
            {
                running = Task.Run(() => RefreshCoreAsync(onBehalfOf), CancellationToken.None);

                // Observed here, so a fault nobody is left waiting for (every caller gave up) is not an
                // unobserved task exception. Each caller still waiting gets it from WaitAsync.
                _ = running.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _signingIn = running;
                _signingInFor = onBehalfOf;
            }
        }

        return running.WaitAsync(ct);
    }

    private async Task<string?> RefreshCoreAsync(EgressPurpose? onBehalfOf)
    {
        if (_erased)
        {
            return ErasedSignIn;
        }

        if (_credentials.Load() is not { } credential)
        {
            return "This device is not activated yet. Enter the code from your invite.";
        }

        if (_http.BaseAddress is null)
        {
            return NoBackendNote ?? "No backend is configured, so this device cannot sign in.";
        }

        // Its own purpose, never Publish: the background renewal used to go out under Publish, so local-only
        // did not stop it (2026-11-15 review). What it is for travels with it, and the guard decides on that.
        using var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/devices/token", UriKind.Relative), EgressPurpose.DeviceAuth);
        if (onBehalfOf is { } behalf)
        {
            _ = request.OnBehalfOf(behalf);
        }

        request.Content = JsonContent.Create(new RefreshBody(credential.RefreshToken), options: Json);

        // A sign-in in front of a publish is waited on by the technician: twenty seconds, not the client's
        // hundred, before saying the backend did not answer.
        // Stopping ends it too, answered rather than thrown: a stop is not a failed renewal.
        using var deadline = new CancellationTokenSource(SignInTimeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, Stopping);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (EgressBlockedException blocked)
        {
            return blocked.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return Stopping.IsCancellationRequested ? StoppingSignIn : NotAnswered;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Revoked = true;
                Forget();
                return RevokedStanding;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return $"The backend answered {(int)response.StatusCode} when this device signed in.";
            }

            TokenAnswer? answer;
            try
            {
                answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(Json, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                answer = null;
            }
            catch (OperationCanceledException)
            {
                return Stopping.IsCancellationRequested ? StoppingSignIn : NotAnswered;
            }

            if (answer is null || string.IsNullOrEmpty(answer.AccessToken))
            {
                return "The backend answered something this version does not understand.";
            }

            if (!Keep(credential with { LastContactAt = _time.GetUtcNow() }, answer.AccessToken, answer.ExpiresAt))
            {
                return ErasedSignIn;
            }

            Revoked = false;
            return null;
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
