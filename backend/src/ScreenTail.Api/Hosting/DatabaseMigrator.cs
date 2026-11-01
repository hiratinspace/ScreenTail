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

    /// <summary>
    /// The record of which migrations ran is the schema owner's alone. The deployment's default
    /// privileges give the API's role writes on every table the owner creates, this one included, and a
    /// role that could edit it could make a later migration be skipped. So every other role's
    /// INSERT, UPDATE, DELETE and TRUNCATE on it is revoked after each run; reading it stays allowed.
    /// </summary>
    internal const string HistoryWritesRevoked = """
        DO $$
        DECLARE grantee_name text;
        BEGIN
            FOR grantee_name IN
                SELECT DISTINCT grantee FROM information_schema.role_table_grants
                WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'
                  AND grantee <> current_user
                  AND privilege_type IN ('INSERT', 'UPDATE', 'DELETE', 'TRUNCATE')
            LOOP
                EXECUTE format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public.%I FROM %I', '__EFMigrationsHistory', grantee_name);
            END LOOP;
        END $$;
        """;

    /// <summary>Applies what is pending and returns the names of the migrations it applied.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(string connection, CancellationToken ct)
    {
        await using var db = new ScreenTailContext(new DbContextOptionsBuilder<ScreenTailContext>().UseNpgsql(connection).Options);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        _ = await db.Database.ExecuteSqlRawAsync(HistoryWritesRevoked, ct).ConfigureAwait(false);
        return pending;
    }
}
