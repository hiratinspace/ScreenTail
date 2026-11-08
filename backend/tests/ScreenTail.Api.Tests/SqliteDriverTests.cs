using Microsoft.Data.Sqlite;

namespace ScreenTail.Api.Tests;

/// <summary>
/// The SQLite driver under <see cref="ApiFixture"/> is one whose pool cannot lend a handle twice.
///
/// Up to 10.0.12, Microsoft.Data.Sqlite marked a pooled connection active before it recorded the owner,
/// outside the pool's lock. A second open that found the pool empty went looking for leaked connections
/// in that window, took the half-activated one for a leak, and put it back in the pool, so two open
/// connections ended up on one native handle (dotnet/efcore#39008, fixed in 10.0.13). CostCapTests sends
/// fifty requests at once through a pooled fixture, and on 2026-11-05 one of them opened on a handle
/// another was still running a statement on: EF's collation could not be registered, "unable to
/// delete/modify collation sequence due to active statements".
///
/// The fixture keeps pooling, and the fix is the driver. This test is the floor under it: EF's SQLite
/// provider still asks for 10.0.12, and a package tidy-up that drops the pin would otherwise bring the
/// race back as a one-in-a-few-hundred-runs failure somewhere else.
/// </summary>
public sealed class SqliteDriverTests
{
    [Fact]
    public void TheTestDatabaseDriverHasThePoolFix()
    {
        var driver = typeof(SqliteConnection).Assembly.GetName().Version!;

        Assert.True(
            driver >= new Version(10, 0, 13),
            $"Microsoft.Data.Sqlite {driver} can hand one pooled handle to two connections (dotnet/efcore#39008). Keep the pin in Directory.Packages.props at 10.0.13 or later.");
    }
}
