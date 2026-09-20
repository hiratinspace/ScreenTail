using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tests;

/// <summary>
/// The API with a real database behind it, on any machine (ST-008).
///
/// SQLite rather than Postgres, over the same EF model: every query in the service is compiled and run
/// for real, on a build machine with no database server. What SQLite cannot check is the migration SQL,
/// so CI applies the real migrations to a real Postgres in a separate step. Between them, nothing about
/// the data layer is taken on trust.
///
/// The signing key here is a test key and exists only in this file. The service has no default one and
/// refuses to start without it, which is the behaviour <c>StartupRefusesAWeakSigningKey</c> asserts.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "test-signing-key-that-is-long-enough-32+";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public JwtOptions Jwt { get; } = new()
    {
        Issuer = "screentail-tests",
        Audience = "screentail-api",
        SigningKey = SigningKey,
    };

    public TokenIssuer Issuer => new(Jwt);

    public async ValueTask InitializeAsync()
    {
        // Held open for the life of the fixture: an in-memory SQLite database exists only while a
        // connection to it does.
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
        _ = await db.Database.EnsureCreatedAsync();
    }

    public async Task<T> UseAsync<T>(Func<ScreenTailContext, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ScreenTailContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Anything EF or Npgsql put in the container, so the SQLite provider is the only one left.</summary>
    private static bool IsEntityFramework(ServiceDescriptor registration) =>
        registration.ServiceType == typeof(ScreenTailContext)
        || registration.ServiceType == typeof(DbContextOptions)
        || registration.ServiceType == typeof(DbContextOptions<ScreenTailContext>)
        || Namespaced(registration.ServiceType)
        || Namespaced(registration.ImplementationType)
        || Namespaced(registration.ImplementationInstance?.GetType());

    private static bool Namespaced(Type? type) =>
        type?.Namespace is { } ns
        && (ns.StartsWith("Npgsql", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Jwt:Issuer", Jwt.Issuer);
        builder.UseSetting("Jwt:Audience", Jwt.Audience);
        builder.UseSetting("Jwt:SigningKey", SigningKey);

        // Satisfies the startup check; the registration below replaces the provider before anything
        // opens it, so no Postgres is ever contacted.
        builder.UseSetting("ConnectionStrings:Postgres", "Host=never-opened;Database=screentail");
        builder.UseEnvironment(Environments.Development);

        // No drafting provider, said out loud rather than left to the machine.
        //
        // The host runs as Development so that it reads user secrets, which is where a developer's real
        // provider key lives. Without this line these tests inherit that key, call Google for real, and
        // bill somebody for every run — and they pass or fail depending on whose laptop they are on.
        // Pinned to empty so the suite behaves the same on a developer machine as in CI.
        builder.UseSetting("Summarization:ApiKey", string.Empty);
        builder.UseSetting("Summarization:FallbackApiKey", string.Empty);

        builder.ConfigureServices(services =>
        {
            // AddDbContext registers the provider's whole service graph, not just the options, and EF
            // refuses to hold two providers at once. Removing the options alone leaves Npgsql's services
            // behind and the host fails to start with a message about two providers rather than about
            // anything a test did.
            foreach (var registration in services
                .Where(IsEntityFramework)
                .ToList())
            {
                _ = services.Remove(registration);
            }

            services.AddDbContext<ScreenTailContext>(options => options.UseSqlite(_connection));
        });
    }
}
