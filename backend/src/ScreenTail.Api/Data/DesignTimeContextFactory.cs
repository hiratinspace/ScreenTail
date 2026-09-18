using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ScreenTail.Api.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without a database (ST-008).
///
/// Migrations are generated from the model, not from a live server, so the connection string here is a
/// placeholder and is never opened. It exists because the application registers the context only when a
/// connection string is configured — a deployment without one should fail to reach the database, not
/// fail to start the tooling.
/// </summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<ScreenTailContext>
{
    public ScreenTailContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScreenTailContext>()
            .UseNpgsql("Host=localhost;Database=screentail;Username=postgres")
            .Options);
}
