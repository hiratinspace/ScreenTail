using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <summary>What one sweep did.</summary>
/// <param name="Upgraded">Legacy rows sealed again in the bound form.</param>
/// <param name="Suspect">Legacy rows found after an earlier sweep had found none left, so written since: warned about, not rewritten.</param>
/// <param name="Refused">Legacy rows left as they were because <c>Vault:AllowLegacyRows</c> is off.</param>
/// <param name="NotOpened">Legacy rows that did not open, or are under a key this deployment does not hold: warned about, left as they are, and the vault is not recorded clear while any is left.</param>
public sealed record VaultSweepResult(int Upgraded, IReadOnlyList<Guid> Suspect, IReadOnlyList<Guid> Refused, IReadOnlyList<Guid> NotOpened);

/// <summary>
/// The startup sweep (2026-11-15 review): once, when the service starts, every integration row sealed
/// before secrets were bound to their row is sealed again in the bound form, and the first time none is
/// left that is recorded (<see cref="VaultState.LegacyRowsClearedAt"/>). Legacy rows only exist from
/// before this change, so a legacy row seen after that was written since, by something other than this
/// service: it is left as it is and named in a warning by row and tenant id, nothing more (INV-10).
///
/// Switched off with <c>Vault:UpgradeOnStart=false</c>. A failure is logged and the service carries on:
/// each legacy row is also upgraded the first time it is opened.
/// </summary>
public sealed partial class VaultSweep(IServiceScopeFactory scopes, VaultOptions options, TimeProvider time, ILogger<VaultSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured || !options.UpgradeOnStart)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await SweepAsync(scope.ServiceProvider.GetRequiredService<ScreenTailContext>(), options, time, logger, stoppingToken).ConfigureAwait(false);
            if (result.Upgraded > 0)
            {
                Upgraded(logger, result.Upgraded);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SweepFailed(logger, e.GetType().Name);
        }
    }

    /// <summary>One sweep, as the service runs it at startup.</summary>
    public static async Task<VaultSweepResult> SweepAsync(ScreenTailContext db, VaultOptions options, TimeProvider time, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        var state = await db.VaultStates.SingleOrDefaultAsync(s => s.Id == VaultState.Only, ct).ConfigureAwait(false);
        var legacy = await db.Integrations.AsNoTracking()
            .Where(i => i.BindingVersion < Envelope.BindingVersion)
            .Select(i => new { i.Id, i.TenantId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (state?.LegacyRowsClearedAt is not null && legacy.Count > 0)
        {
            foreach (var row in legacy)
            {
                LegacyAfterClear(logger, row.Id, row.TenantId);
            }

            return new VaultSweepResult(0, [.. legacy.Select(r => r.Id)], [], []);
        }

        var rewrite = legacy.Count == 0 ? new VaultRewrite(0, [], []) : await KeyRotation.UpgradeLegacyAsync(db, options, time, ct).ConfigureAwait(false);
        foreach (var id in rewrite.RefusedLegacy)
        {
            LegacyRefused(logger, id, legacy.Single(r => r.Id == id).TenantId);
        }

        // Counted again rather than worked out: a row under a key this deployment does not hold is neither
        // upgraded nor refused, and one that does not open is reported and passed over, and either way it
        // is still legacy. The vault is recorded clear only when the database says none is left.
        var left = await db.Integrations.AsNoTracking()
            .Where(i => i.BindingVersion < Envelope.BindingVersion)
            .Select(i => new { i.Id, i.TenantId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var row in left.Where(r => !rewrite.RefusedLegacy.Contains(r.Id)))
        {
            LegacyNotOpened(logger, row.Id, row.TenantId);
        }

        if (left.Count == 0)
        {
            if (state is null)
            {
                state = new VaultState { Id = VaultState.Only };
                db.VaultStates.Add(state);
            }

            state.LegacyRowsClearedAt ??= time.GetUtcNow();
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new VaultSweepResult(rewrite.Changed, [], rewrite.RefusedLegacy, [.. left.Where(r => !rewrite.RefusedLegacy.Contains(r.Id)).Select(r => r.Id)]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vault sweep: {Count} integration credential(s) stored before secrets were bound to their row were sealed again in the bound form.")]
    private static partial void Upgraded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vault sweep: integration row {RowId} of tenant {TenantId} is marked as sealed before secrets were bound to their row, but every such row was already upgraded, so it was written since. It was not changed. Ask the owner to connect that integration again.")]
    private static partial void LegacyAfterClear(ILogger logger, Guid rowId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vault sweep: integration row {RowId} of tenant {TenantId} was sealed before secrets were bound to their row and Vault:AllowLegacyRows is off, so it was not changed or opened.")]
    private static partial void LegacyRefused(ILogger logger, Guid rowId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vault sweep: integration row {RowId} of tenant {TenantId} was sealed before secrets were bound to their row and does not open under the keys this deployment holds, so it was left as it is. Ask the owner to connect that integration again.")]
    private static partial void LegacyNotOpened(ILogger logger, Guid rowId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vault sweep did not finish ({ExceptionType}). Legacy rows are still upgraded as they are opened.")]
    private static partial void SweepFailed(ILogger logger, string exceptionType);
}
