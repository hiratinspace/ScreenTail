using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// How many drafts one device and one tenant may have in flight at once (2026-10-12 review, E-35).
///
/// The endpoint's own tests show the gate is in front of the body. These pin the arithmetic: the two
/// limits, that a place is given back exactly once, and that nothing is kept for a device that has
/// stopped drafting.
/// </summary>
public sealed class DraftingConcurrencyTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    [Fact]
    public void ADeviceMayHaveTwoDraftsInFlightAndNotAThird()
    {
        var gate = new DraftingConcurrency();
        var device = Guid.NewGuid();

        using var first = gate.TryEnter(TenantA, device, out _);
        using var second = gate.TryEnter(TenantA, device, out _);
        var third = gate.TryEnter(TenantA, device, out var refusal);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
        Assert.Contains("device", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ATenantMayHaveTenDraftsInFlightAcrossItsDevices()
    {
        var gate = new DraftingConcurrency();
        var leases = Enumerable.Range(0, DraftingConcurrency.PerTenant)
            .Select(i => gate.TryEnter(TenantA, Guid.NewGuid(), out _))
            .ToList();

        var over = gate.TryEnter(TenantA, Guid.NewGuid(), out var refusal);
        var elsewhere = gate.TryEnter(TenantB, Guid.NewGuid(), out _);

        Assert.All(leases, Assert.NotNull);
        Assert.Null(over);
        Assert.Contains("tenant", refusal, StringComparison.Ordinal);
        Assert.NotNull(elsewhere);

        elsewhere!.Dispose();
        leases.ForEach(lease => lease!.Dispose());
    }

    [Fact]
    public void ARefusalByTheTenantDoesNotKeepTheDevicesPlace()
    {
        // The device is counted first. Turned away by the tenant, it must not be left holding one of
        // its own two places for a draft that never started.
        var gate = new DraftingConcurrency();
        var leases = Enumerable.Range(0, DraftingConcurrency.PerTenant)
            .Select(i => gate.TryEnter(TenantA, Guid.NewGuid(), out _))
            .ToList();
        var device = Guid.NewGuid();

        Assert.Null(gate.TryEnter(TenantA, device, out _));
        Assert.Null(gate.TryEnter(TenantA, device, out _));
        leases[0]!.Dispose();
        leases[1]!.Dispose();

        using var first = gate.TryEnter(TenantA, device, out _);
        using var second = gate.TryEnter(TenantA, device, out _);
        Assert.NotNull(first);
        Assert.NotNull(second);

        leases.Skip(2).ToList().ForEach(lease => lease!.Dispose());
    }

    [Fact]
    public void APlaceIsGivenBackOnceHoweverOftenTheLeaseIsDisposed()
    {
        var gate = new DraftingConcurrency();
        var device = Guid.NewGuid();

        var first = gate.TryEnter(TenantA, device, out _)!;
        using var second = gate.TryEnter(TenantA, device, out _);
        first.Dispose();
        first.Dispose();

        using var third = gate.TryEnter(TenantA, device, out _);
        Assert.NotNull(third);
        Assert.Null(gate.TryEnter(TenantA, device, out _));
    }

    [Fact]
    public void NothingIsKeptForADeviceThatHasStoppedDrafting()
    {
        // Bounded by what is in flight, not by how many devices have ever asked.
        var gate = new DraftingConcurrency();
        for (var i = 0; i < 1_000; i++)
        {
            gate.TryEnter(Guid.NewGuid(), Guid.NewGuid(), out _)!.Dispose();
        }

        Assert.Equal(0, gate.Tracked);
    }

    [Fact]
    public async Task ManyAtOnceNeverLetMoreThroughThanTheLimit()
    {
        var gate = new DraftingConcurrency();
        var device = Guid.NewGuid();
        var inside = 0;
        var most = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(
            async () =>
            {
                using var lease = gate.TryEnter(TenantA, device, out _);
                if (lease is null)
                {
                    return;
                }

                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                await Task.Delay(5, TestContext.Current.CancellationToken);
                _ = Interlocked.Decrement(ref inside);
            },
            TestContext.Current.CancellationToken)));

        Assert.InRange(most, 1, DraftingConcurrency.PerDevice);
        Assert.Equal(0, gate.Tracked);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
