using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Summarize;

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
/// refuses to start without it, which is the behaviour <c>TheHostWillNotServeWithoutASigningKey</c> asserts.
/// </summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "test-signing-key-that-is-long-enough-32+";

    /// <summary>The vault's master key for this host, or null for a host with none (ST-009).</summary>
    protected virtual string? VaultMasterKey { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    // A database file of the fixture's own, opened by a new connection per scope. It used to be one
    // in-memory connection shared by every request, and SqliteConnection is not thread-safe: tests that
    // send requests at once (CostCapTests sends fifty) corrupted its bookkeeping, and closing it then
    // threw from SqliteConnection.Close, reported as a class cleanup failure about one full run in
    // forty. WAL lets readers run beside the one writer, and a writer waits for the lock as Postgres
    // would make it wait for a row.
    private readonly string _database = Path.Combine(Path.GetTempPath(), "screentail-api-tests", $"{Guid.NewGuid():N}.db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _database, Pooling = true }.ToString();

    public JwtOptions Jwt { get; } = new()
    {
        Issuer = "screentail-tests",
        Audience = "screentail-api",
        SigningKey = SigningKey,
    };

    public TokenIssuer Issuer => new(Jwt);

    public async ValueTask InitializeAsync()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_database)!);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            _ = await wal.ExecuteScalarAsync();
        }

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
        // The host first, so the requests it is still serving finish; then the pooled connections, so the
        // files can go.
        await base.DisposeAsync();
        using (var connection = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var file in new[] { _database, _database + "-wal", _database + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temp file left behind is not a test failure.
            }
        }

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

        // And the provider, for the same reason: a developer who drafts on Claude keeps
        // Summarization:Provider=claude in the same user secrets, and the wiring tests would then be
        // reading their machine rather than the code. A test that wants Claude says so.
        builder.UseSetting("Summarization:Provider", SummarizationOptions.Gemini);
        builder.UseSetting("Vault:MasterKey", VaultMasterKey ?? string.Empty);

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

            services.AddDbContext<ScreenTailContext>(options => options.UseSqlite(ConnectionString));
        });
    }
}
