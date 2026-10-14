using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Core.Net;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Net;

/// <summary>
/// The device's side of ST-010: activation with an invite's code, the refresh token kept and the access
/// token renewed an hour at a time, the environment's token as the fallback the M1 runbook still uses,
/// and the seven-day grace (AC3) counted from the last time the backend answered.
/// </summary>
public sealed class DeviceSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActivationPostsTheCodeAndKeepsTheRefreshTokenAndTenant()
    {
        var backend = new ScriptedBackend();
        backend.Reply(Json(new { tenantName = "Acme IT", deviceId = "0d9f3c6e-1111-4c1e-9a1e-000000000001", refreshToken = "refresh-1", accessToken = "access-1", expiresAt = T0.AddHours(1) }));
        var credentials = new InMemoryCredentials();
        var session = new DeviceSession(Client(backend), credentials, time: new ManualTime(T0));

        var answer = await session.ActivateAsync("kx7pm-4r2wq", "TECH-LAPTOP", TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal("Acme IT", answer.Value!.TenantName);
        Assert.EndsWith("/v1/devices/activate", backend.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"kx7pm-4r2wq\"", backend.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"deviceName\":\"TECH-LAPTOP\"", backend.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("refresh-1", credentials.Load()!.RefreshToken);
        Assert.Equal("Acme IT", credentials.Load()!.TenantName);
        Assert.Equal("access-1", session.CurrentToken);
        Assert.True(session.Activated);
    }

    [Fact]
    public async Task ARefusedCodeIsTheBackendsSentence()
    {
        var backend = new ScriptedBackend();
        backend.Reply(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "seat_limit", message = "Seat limit reached — contact your admin" }) });
        var credentials = new InMemoryCredentials();
        var session = new DeviceSession(Client(backend), credentials, time: new ManualTime(T0));

        var answer = await session.ActivateAsync("KX7PM-4R2WQ", "TECH-LAPTOP", TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal("Seat limit reached — contact your admin", answer.Refusal);
        Assert.Null(credentials.Load());
        Assert.False(session.Activated);
    }

    [Fact]
    public async Task TheRefreshTokenBuysANewAccessTokenBeforeTheOldOneRunsOut()
    {
        var backend = new ScriptedBackend();
        backend.Reply(Json(new { accessToken = "access-2", expiresAt = T0.AddHours(2) }));
        var credentials = new InMemoryCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0.AddHours(-1)));
        var time = new ManualTime(T0);
        var session = new DeviceSession(Client(backend), credentials, time: time);
        Assert.Null(session.CurrentToken);

        Assert.True(await session.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal("access-2", session.CurrentToken);
        Assert.EndsWith("/v1/devices/token", backend.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"refreshToken\":\"refresh-1\"", backend.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(T0, credentials.Load()!.LastContactAt);

        // Five minutes before expiry the token is treated as gone, so a request in flight at the
        // boundary is not sent with a token the backend is about to refuse.
        time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(4));
        Assert.Null(session.CurrentToken);
    }

    [Fact]
    public async Task ARevokedDeviceLosesItsTokenAndSaysSo()
    {
        var backend = new ScriptedBackend();
        backend.Reply(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var credentials = new InMemoryCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));
        var session = new DeviceSession(Client(backend), credentials, time: new ManualTime(T0));

        Assert.False(await session.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Null(session.CurrentToken);
        Assert.True(session.Revoked);
        Assert.Contains("revoked", session.Standing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutACredentialTheEnvironmentsTokenIsUsedAndNothingIsAsked()
    {
        var backend = new ScriptedBackend();
        var session = new DeviceSession(Client(backend), new InMemoryCredentials(), () => "env-token", new ManualTime(T0));

        Assert.Equal("env-token", session.CurrentToken);
        Assert.False(await session.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(backend.Requests);
        Assert.False(session.Activated);
    }

    [Fact]
    public async Task SevenDaysWithoutAnAnswerIsBeyondGraceAndTheDaysAreCounted()
    {
        var backend = new ScriptedBackend();
        backend.Reply(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var credentials = new InMemoryCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));
        var time = new ManualTime(T0.AddDays(6));
        var session = new DeviceSession(Client(backend), credentials, time: time);

        Assert.False(await session.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(session.Revoked);
        Assert.Equal(6, session.DaysOffline);
        Assert.False(session.BeyondGrace);

        time.Advance(TimeSpan.FromDays(1.5));
        Assert.True(session.BeyondGrace);
        Assert.Contains("7 days", session.Standing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteEverythingForgetsTheRefreshTokenAndTheAccessToken()
    {
        // INV-12: "delete everything" removes the device's tokens. The file goes with the rest of the data
        // directory; this is the copy the running service holds, and the credential store's own Clear.
        var backend = new ScriptedBackend();
        backend.Reply(Json(new { tenantName = "Acme IT", deviceId = "0d9f3c6e-1111-4c1e-9a1e-000000000001", refreshToken = "refresh-1", accessToken = "access-1", expiresAt = T0.AddHours(1) }));
        var credentials = new InMemoryCredentials();
        var session = new DeviceSession(Client(backend), credentials, time: new ManualTime(T0));
        Assert.True((await session.ActivateAsync("kx7pm-4r2wq", "TECH-LAPTOP", TestContext.Current.CancellationToken)).Ok);

        session.Erase();

        Assert.Null(credentials.Load());
        Assert.Null(session.CurrentToken);
        Assert.False(session.Activated);
    }

    [Fact]
    public async Task AfterDeleteEverythingNothingIsSentNotEvenTheEnvironmentsToken()
    {
        // The service stops straight after, but until it has, nothing it sends should carry a token the
        // technician has just asked to be rid of.
        var backend = new ScriptedBackend();
        var credentials = new InMemoryCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));
        var session = new DeviceSession(Client(backend), credentials, () => "env-token", new ManualTime(T0));

        session.Erase();

        Assert.Null(session.CurrentToken);
        Assert.False(await session.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False((await session.ActivateAsync("kx7pm-4r2wq", "TECH-LAPTOP", TestContext.Current.CancellationToken)).Ok);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task ARenewalThatLandsAfterDeleteEverythingDoesNotWriteTheTokenBack()
    {
        // The renewal loop runs on its own. One that was waiting on the backend when the technician
        // confirmed would otherwise save the refresh token again after it had been removed, and the
        // device would start the next run still enrolled.
        var backend = new HeldBackend();
        var credentials = new InMemoryCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));
        var session = new DeviceSession(new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") }, credentials, time: new ManualTime(T0));

        var renewing = session.RefreshAsync(TestContext.Current.CancellationToken);
        await backend.Arrived.WaitAsync(TestContext.Current.CancellationToken);
        session.Erase();
        backend.Release(Json(new { accessToken = "access-2", expiresAt = T0.AddHours(2) }));

        Assert.False(await renewing);
        Assert.Null(credentials.Load());
        Assert.Null(session.CurrentToken);
    }

    [Fact]
    public async Task AnActivationThatLandsAfterDeleteEverythingIsRefusedAndNotKept()
    {
        var backend = new HeldBackend();
        var credentials = new InMemoryCredentials();
        var session = new DeviceSession(new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") }, credentials, time: new ManualTime(T0));

        var activating = session.ActivateAsync("kx7pm-4r2wq", "TECH-LAPTOP", TestContext.Current.CancellationToken);
        await backend.Arrived.WaitAsync(TestContext.Current.CancellationToken);
        session.Erase();
        backend.Release(Json(new { tenantName = "Acme IT", deviceId = "0d9f3c6e-1111-4c1e-9a1e-000000000001", refreshToken = "refresh-1", accessToken = "access-1", expiresAt = T0.AddHours(1) }));

        var answer = await activating;
        Assert.False(answer.Ok);
        Assert.Null(credentials.Load());
        Assert.Null(session.CurrentToken);
    }

    [Fact]
    public void ACredentialFileThatWillNotGoStillLeavesNothingInUse()
    {
        // Antivirus holding the file. The erase owed on disk finishes the job at the next start; until
        // then the running service has forgotten the token and does not use what is left.
        var credentials = new StubbornCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));
        var session = new DeviceSession(Client(new ScriptedBackend()), credentials, time: new ManualTime(T0));

        session.Erase();

        Assert.Equal(1, credentials.Clears);
        Assert.Null(session.CurrentToken);
    }

    [Fact]
    public async Task ARenewalWhoseCredentialWillNotSaveStillHoldsTheTokenItBought()
    {
        // Antivirus holding device.cred while it is replaced. The renewal fails loudly, as it always has,
        // but the access token the backend has just issued is held first, as it was before "delete
        // everything" shared the gate with the save (2026-10-12 review, round 2).
        var backend = new ScriptedBackend();
        backend.Reply(Json(new { accessToken = "access-2", expiresAt = T0.AddHours(2) }));
        var credentials = new StubbornCredentials(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0.AddHours(-1)));
        var session = new DeviceSession(Client(backend), credentials, time: new ManualTime(T0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal("access-2", session.CurrentToken);
    }

    private static HttpClient Client(ScriptedBackend backend) => new(backend) { BaseAddress = new Uri("https://api.screentail.example/") };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    private sealed class ScriptedBackend : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _replies = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public void Reply(HttpResponseMessage reply) => _replies.Enqueue(reply);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _replies.Count > 0 ? _replies.Dequeue() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Answers each request only when the test says so, to put a reply after something else.</summary>
    private sealed class HeldBackend : HttpMessageHandler
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release(HttpResponseMessage reply) => _reply.SetResult(reply);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _arrived.TrySetResult();
            return _reply.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StubbornCredentials(DeviceCredential initial) : IDeviceCredentials
    {
        public int Clears { get; private set; }

        public DeviceCredential? Load() => initial;

        public void Save(DeviceCredential credential) => throw new InvalidOperationException("Nothing is saved in this test.");

        public void Clear()
        {
            Clears++;
            throw new IOException("The file is in use by another process.");
        }
    }

    internal sealed class InMemoryCredentials(DeviceCredential? initial = null) : IDeviceCredentials
    {
        private DeviceCredential? _stored = initial;

        public DeviceCredential? Load() => _stored;

        public void Save(DeviceCredential credential) => _stored = credential;

        public void Clear() => _stored = null;
    }
}
