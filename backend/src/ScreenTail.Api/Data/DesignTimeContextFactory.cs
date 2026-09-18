using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ScreenTail.Api.Data;

/// <summary>
/// How <c>dotnet ef</c> builds the model (ST-008).
///
/// EF prefers a factory like this over starting the application, which is what makes
/// <c>migrations add</c> work on a machine with no database: migrations are generated from the model, not
/// from a live server, so the placeholder below is never opened.
///
/// <b>It reads the real connection string when there is one.</b> Because EF prefers this factory,
/// ignoring the environment would mean <c>database update</c> silently used the placeholder — which is
/// how the first CI run of this failed, with "no password has been provided" pointing at a connection
/// string nobody had configured.
/// </summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<ScreenTailContext>
{
    /// <summary>Enough to build the model, and deliberately not enough to reach anything.</summary>
    private const string ModelOnly = "Host=localhost;Database=screentail;Username=postgres";

    public ScreenTailContext CreateDbContext(string[] args)
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        return new ScreenTailContext(new DbContextOptionsBuilder<ScreenTailContext>()
            .UseNpgsql(string.IsNullOrWhiteSpace(configured) ? ModelOnly : configured)
            .Options);
    }
}
