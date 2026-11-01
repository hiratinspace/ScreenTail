using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// Two activations at once (ST-010, review of #248). SQLite runs one writer at a time, so two requests
/// sent together would simply queue and prove nothing. Instead the activation under test runs on a host
/// with an interceptor that, right after a chosen read, sends a second activation through the suite's
/// own host (which has none) and lets it run: the interleaving that a race needs, made to happen every
/// time. One code must enrol one device, and the last seat must go to one of them.
/// </summary>
public sealed class ActivationRaceTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly Uri Activate = new("/v1/devices/activate", UriKind.Relative);

    [Fact]
    public async Task OfTwoActivationsWithOneCodeExactlyOneEnrolsADevice()
    {
        // The rival runs after this request has read the invite as unclaimed and before it claims it.
        var (tenantId, invites) = await TenantAsync(seats: 5, invites: 1);
        var code = invites[0].Code;
        using var rival = api.CreateClient();
        HttpStatusCode? rivalStatus = null;
        var racer = new AfterRead(
            sql => sql.Contains("FROM \"invites\"", StringComparison.Ordinal) && sql.Contains("\"CodeHash\"", StringComparison.Ordinal),
            async () =>
            {
                using var response = await rival.PostAsJsonAsync(Activate, new ActivateDeviceRequest(code, "RIVAL"), TestContext.Current.CancellationToken);
                rivalStatus = response.StatusCode;
            },
            waitFor: Timeout.InfiniteTimeSpan);
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = host.CreateClient();

        using var mine = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(code, "MINE"), TestContext.Current.CancellationToken);

        Assert.True(racer.Fired);
        Assert.Equal(HttpStatusCode.OK, rivalStatus);
        Assert.Equal(HttpStatusCode.BadRequest, mine.StatusCode);
        Assert.Equal("invite_used", (await mine.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("error").GetString());
        var devices = await api.UseAsync(db => db.Devices.Where(d => d.TenantId == tenantId).Select(d => new { d.Id, d.Name }).ToListAsync());
        var device = Assert.Single(devices);
        Assert.Equal("RIVAL", device.Name);
        Assert.Equal(device.Id, await api.UseAsync(db => db.Invites.Where(i => i.Id == invites[0].Id).Select(i => i.DeviceId).SingleAsync()));
    }

    [Fact]
    public async Task TwoActivationsForTheLastSeatDoNotBothTakeIt()
    {
        // Two codes, one seat. The rival starts after this request has counted the seats and before it
        // adds its device, and is given half a second: without a lock it finishes in that time and both
        // devices land; with one it waits for this request's commit and then finds the seat taken.
        var (tenantId, invites) = await TenantAsync(seats: 1, invites: 2);
        using var rival = api.CreateClient();
        Task<HttpResponseMessage>? rivalCall = null;
        var racer = new AfterRead(
            sql => sql.Contains("COUNT(*)", StringComparison.Ordinal) && sql.Contains("FROM \"devices\"", StringComparison.Ordinal),
            () =>
            {
                rivalCall = rival.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[1].Code, "RIVAL"), TestContext.Current.CancellationToken);
                return rivalCall;
            },
            waitFor: TimeSpan.FromMilliseconds(500));
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = host.CreateClient();

        using var mine = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "MINE"), TestContext.Current.CancellationToken);
        Assert.True(racer.Fired);
        using var theirs = await rivalCall!;

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, theirs.StatusCode);
        Assert.Contains(DevicesEndpoint.SeatLimitMessage, await theirs.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == tenantId && d.RevokedAt == null)));

        // The refused code was not used up: it works once a seat is free.
        Assert.Null(await api.UseAsync(db => db.Invites.Where(i => i.Id == invites[1].Id).Select(i => i.AcceptedAt).SingleAsync()));
    }

    [Fact]
    public async Task ACodeRefusedForTheSeatLimitIsNotUsedUp()
    {
        var (tenantId, invites) = await TenantAsync(seats: 1, invites: 2);
        using var client = api.CreateClient();
        using var first = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "FIRST"), TestContext.Current.CancellationToken);
        using var full = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[1].Code, "SECOND"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);

        _ = await api.UseAsync(db => db.Devices.Where(d => d.TenantId == tenantId).ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));
        using var seated = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[1].Code, "SECOND"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);
    }

    [Fact]
    public async Task TheTenantIsLockedInsideTheTransactionBeforeTheSeatsAreCounted()
    {
        // On SQLite the transaction alone serialises writers, so the race tests above would pass without
        // the tenant's row lock; on Postgres they would not. This pins the lock itself: an UPDATE of the
        // tenant's row, in the activation's transaction, before the invite is claimed and the seats
        // counted (PostgresActivationRaceTests runs the race on Postgres when one is configured).
        var (_, invites) = await TenantAsync(seats: 2, invites: 1);
        var recorder = new Recorder();
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(recorder))));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "LOCKED"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var commands = recorder.Commands.ToList();
        var locking = commands.FindIndex(c => IsTenantLock(c.Sql));
        var claiming = commands.FindIndex(c => c.Sql.StartsWith("UPDATE \"invites\"", StringComparison.Ordinal));
        var counting = commands.FindIndex(c => c.Sql.Contains("COUNT(*)", StringComparison.Ordinal) && c.Sql.Contains("FROM \"devices\"", StringComparison.Ordinal));
        Assert.True(locking >= 0, "no UPDATE of the tenant's row");
        Assert.True(locking < claiming && claiming < counting, $"lock {locking}, claim {claiming}, count {counting}");
        Assert.NotNull(commands[locking].Transaction);
        Assert.Same(commands[locking].Transaction, commands[claiming].Transaction);
        Assert.Same(commands[locking].Transaction, commands[counting].Transaction);
    }

    [Fact]
    public async Task OffboardingLocksTheTenantFirstInTheOrderActivationDoes()
    {
        // Activation locks the tenant's row and then writes the invite; offboarding must not hold the
        // invites and then wait for the tenant, or the two could each wait for the other.
        var (tenantId, _) = await TenantAsync(seats: 1, invites: 2);
        var recorder = new Recorder();
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(recorder))));
        using var scope = host.Services.CreateScope();

        var gone = await Offboarding.DeleteTenantAsync(scope.ServiceProvider.GetRequiredService<ScreenTailContext>(), tenantId, TestContext.Current.CancellationToken);

        Assert.Equal(3, gone.Rows);
        var commands = recorder.Commands.ToList();
        Assert.True(IsTenantLock(commands[0].Sql), commands[0].Sql);
        Assert.NotNull(commands[0].Transaction);
        Assert.All(commands, c => Assert.Same(commands[0].Transaction, c.Transaction));
        Assert.True(commands.FindIndex(c => c.Sql.StartsWith("DELETE FROM \"invites\"", StringComparison.Ordinal)) > 0);
    }

    [Fact]
    public async Task AnInviteThatExpiresBetweenTheReadAndTheClaimIsExpiredNotUsed()
    {
        var clock = new HeldClock();
        var (_, invites) = await TenantAsync(seats: 5, invites: 1);
        var racer = new AfterRead(
            sql => sql.Contains("FROM \"invites\"", StringComparison.Ordinal) && sql.Contains("\"CodeHash\"", StringComparison.Ordinal),
            () =>
            {
                clock.Advance(Invites.Lifetime + TimeSpan.FromMinutes(1));
                return Task.CompletedTask;
            },
            waitFor: Timeout.InfiniteTimeSpan);
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            clock.Install(s);
            _ = s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer));
        }));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "LATE"), TestContext.Current.CancellationToken);

        Assert.True(racer.Fired);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invite_expired", body.GetProperty("error").GetString());
        Assert.Equal(DevicesEndpoint.ExpiredCodeMessage, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ATenantOffboardedBetweenTheReadAndTheLockIsAnUnknownCode()
    {
        var (tenantId, invites) = await TenantAsync(seats: 5, invites: 1);
        var racer = new AfterRead(
            sql => sql.Contains("FROM \"invites\"", StringComparison.Ordinal) && sql.Contains("\"CodeHash\"", StringComparison.Ordinal),
            () => api.UseAsync(db => Offboarding.DeleteTenantAsync(db, tenantId)),
            waitFor: Timeout.InfiniteTimeSpan);
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "GONE"), TestContext.Current.CancellationToken);

        Assert.True(racer.Fired);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invite_unknown", (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("error").GetString());
        Assert.Equal(0, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == tenantId)));
    }

    private static bool IsTenantLock(string sql) =>
        sql.StartsWith("UPDATE \"tenants\"", StringComparison.Ordinal) && sql.Contains("\"Seats\"", StringComparison.Ordinal);

    private async Task<(Guid TenantId, IReadOnlyList<IssuedInvite> Invites)> TenantAsync(int seats, int invites)
    {
        var tenantId = Guid.NewGuid();
        var issued = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Racing IT", Seats = seats, CreatedAt = DateTimeOffset.UtcNow });
            _ = await db.SaveChangesAsync();
            var made = new List<IssuedInvite>();
            for (var i = 0; i < invites; i++)
            {
                made.Add(await Invites.CreateAsync(db, tenantId, $"racer{i}@racing.example", $"Racer {i}", TimeProvider.System));
            }

            return made;
        });
        return (tenantId, issued);
    }

    /// <summary>Every command the context sends, with the transaction it ran in, in order.</summary>
    private sealed class Recorder : DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Sql, DbTransaction? Transaction)> Commands { get; } = new();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            Commands.Enqueue((command.CommandText.TrimStart(), command.Transaction));
        }
    }

    /// <summary>
    /// Once, right after the first command whose SQL matches has run, starts <paramref name="rival"/> and
    /// waits for it (or for <paramref name="waitFor"/>, whichever is first) before the request under test
    /// goes on.
    /// </summary>
    internal sealed class AfterRead(Func<string, bool> matches, Func<Task> rival, TimeSpan waitFor) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            await RunRivalAsync(command.CommandText).ConfigureAwait(false);
            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            await RunRivalAsync(command.CommandText).ConfigureAwait(false);
            return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private async Task RunRivalAsync(string sql)
        {
            if (!matches(sql) || Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return;
            }

            var running = rival();
            if (waitFor == Timeout.InfiniteTimeSpan)
            {
                await running.ConfigureAwait(false);
            }
            else
            {
                _ = await Task.WhenAny(running, Task.Delay(waitFor)).ConfigureAwait(false);
            }
        }
    }
}
