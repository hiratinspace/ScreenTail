using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Hosting;

/// <summary>
/// <c>--migrate</c>: applies the migrations compiled into this build and exits (ST-007). The service never
/// migrates on its own at startup; a deployment runs this as a separate, deliberate step
/// (<c>docker compose run --rm migrate</c>, docs/dev/deploy.md), with a database role that may change the
/// schema, while the API itself connects as a role that may only read and write rows.
///
/// It needs the connection string and nothing else, so it runs before the signing key and every other
/// setting are checked. Applying twice is a no-op, and EF takes a lock so two runs cannot interleave.
/// </summary>
public static class DatabaseMigrator
{
    public const string Argument = "--migrate";

    /// <summary>Applies what is pending and returns the names of the migrations it applied.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(string connection, CancellationToken ct)
    {
        await using var db = new ScreenTailContext(new DbContextOptionsBuilder<ScreenTailContext>().UseNpgsql(connection).Options);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        return pending;
    }
}
