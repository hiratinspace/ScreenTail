using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// The API over a real Postgres, in a database of its own that it creates and drops, when
/// <see cref="Variable"/> names a server (CI's Backend job sets it to the job's Postgres service).
/// Otherwise the host is the suite's usual SQLite one and the tests that need Postgres skip; on CI they
/// fail instead, so the race on the real database is never silently not run.
/// </summary>
public sealed class PostgresApiFixture : ApiFixture
{
    public const string Variable = "SCREENTAIL_TEST_POSTGRES";

    private readonly string? _connection = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } server
        ? new NpgsqlConnectionStringBuilder(server) { Database = $"screentail_race_{Guid.NewGuid():N}", Pooling = true }.ConnectionString
        : null;

    public bool Available => _connection is not null;

    /// <summary>Skips without a server, or fails on CI, where the Backend job always gives one.</summary>
    public void RequirePostgres()
    {
        if (Available)
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{Variable} is not set on CI, so the activation race was not run against Postgres.");
        }

        Assert.Skip($"{Variable} is not set: no Postgres to run the activation race against.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (_connection is null)
        {
            return;
        }

        builder.ConfigureServices(services =>
        {
            foreach (var registration in services.Where(IsEntityFramework).ToList())
            {
                _ = services.Remove(registration);
            }

            services.AddDbContext<ScreenTailContext>(options => options.UseNpgsql(_connection));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            using var scope = Services.CreateScope();
            _ = await scope.ServiceProvider.GetRequiredService<ScreenTailContext>().Database.EnsureDeletedAsync();
            NpgsqlConnection.ClearAllPools();
        }

        await base.DisposeAsync();
    }
}

/// <summary>
/// The activation race on Postgres (ST-010, review of #248). On SQLite the transaction alone serialises
/// writers; on Postgres it does not, and what keeps two activations from both taking a tenant's last seat
/// is the tenant's row lock (<see cref="TenantLock"/>). Here the second activation starts once the first
/// has counted the seats and is given two seconds: with the lock it waits on the tenant's row until the
/// first commits, then finds the seat taken; without it, it counts a free seat (the first's device is
/// not committed yet) and both devices land.
/// </summary>
public sealed class PostgresActivationRaceTests(PostgresApiFixture api) : IClassFixture<PostgresApiFixture>
{
    private static readonly Uri Activate = new("/v1/devices/activate", UriKind.Relative);

    [Fact]
    public async Task TwoActivationsForTheLastSeatDoNotBothTakeItOnPostgres()
    {
        api.RequirePostgres();
        var (tenantId, invites) = await TenantAsync(seats: 1, invites: 2);
        using var rival = api.CreateClient();
        Task<HttpResponseMessage>? rivalCall = null;
        var racer = new ActivationRaceTests.AfterRead(
            sql => sql.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && sql.Contains("devices", StringComparison.Ordinal),
            () =>
            {
                rivalCall = rival.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[1].Code, "RIVAL"), TestContext.Current.CancellationToken);
                return rivalCall;
            },
            waitFor: TimeSpan.FromSeconds(2));
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = host.CreateClient();

        using var mine = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, "MINE"), TestContext.Current.CancellationToken);
        Assert.True(racer.Fired);
        using var theirs = await rivalCall!;

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, theirs.StatusCode);
        Assert.Equal(1, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == tenantId && d.RevokedAt == null)));
        Assert.Null(await api.UseAsync(db => db.Invites.Where(i => i.Id == invites[1].Id).Select(i => i.AcceptedAt).SingleAsync()));
    }

    [Fact]
    public async Task ManyActivationsWithOneCodeAtOnceEnrolOneDeviceOnPostgres()
    {
        api.RequirePostgres();
        var (tenantId, invites) = await TenantAsync(seats: 5, invites: 1);
        using var client = api.CreateClient();

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(invites[0].Code, $"AT-ONCE-{i}"), TestContext.Current.CancellationToken);
            return response.StatusCode;
        }));

        Assert.Equal(1, answers.Count(s => s == HttpStatusCode.OK));
        Assert.All(answers.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.BadRequest, s));
        Assert.Equal(1, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == tenantId)));
    }

    private async Task<(Guid TenantId, IReadOnlyList<IssuedInvite> Invites)> TenantAsync(int seats, int invites)
    {
        var tenantId = Guid.NewGuid();
        var issued = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Postgres Racing IT", Seats = seats, CreatedAt = DateTimeOffset.UtcNow });
            _ = await db.SaveChangesAsync();
            var made = new List<IssuedInvite>();
            for (var i = 0; i < invites; i++)
            {
                made.Add(await Invites.CreateAsync(db, tenantId, $"pg{i}@racing.example", $"Racer {i}", TimeProvider.System));
            }

            return made;
        });
        return (tenantId, issued);
    }
}
