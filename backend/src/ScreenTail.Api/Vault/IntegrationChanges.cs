using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <summary>Who made a change: the owner from the web admin, or a technician from one of their devices.</summary>
public sealed record ChangeActor(Guid? OwnerId, Guid? UserId, Guid? DeviceId)
{
    public static ChangeActor Owner(Guid ownerId) => new(ownerId, null, null);

    public static ChangeActor Technician(Guid userId, Guid deviceId) => new(null, userId, deviceId);
}

/// <param name="Kind"><c>owner</c> or <c>technician</c>.</param>
/// <param name="Name">The owner's or technician's display name, or null if it can no longer be found.</param>
/// <param name="Device">The technician's device name, or null for the owner.</param>
public sealed record ChangedBy(string Kind, string? Name, string? Device);

/// <summary>
/// The record of changes to a tenant's integrations and company mappings (2026-11-15 review). The owner
/// sees it on the web admin's Integrations page: who connected a provider, and every mapping a technician
/// chose while publishing. A row names the provider, what happened, who and when; for a mapping, the
/// companies. It never holds a credential, and nothing here is logged.
/// </summary>
public static class IntegrationChanges
{
    public const string Connected = "connected";
    public const string Replaced = "replaced";
    public const string Disconnected = "disconnected";
    public const string Mapped = "mapped";
    public const string Unmapped = "unmapped";
    public const string MappingsCleared = "mappings_cleared";

    /// <summary>How many of the latest changes the owner's page shows.</summary>
    public const int Shown = 20;

    /// <summary>Adds the change to the context; the caller's save writes it with the change it records.</summary>
    public static void Record(
        ScreenTailContext db,
        Guid tenantId,
        string provider,
        string action,
        ChangeActor by,
        DateTimeOffset at,
        string? psaCompany = null,
        string? docCompanyId = null,
        string? docCompanyName = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(by);
        _ = db.IntegrationChanges.Add(new IntegrationChange
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            At = at,
            Provider = provider,
            Action = action,
            OwnerId = by.OwnerId,
            UserId = by.UserId,
            DeviceId = by.DeviceId,
            PsaCompany = psaCompany,
            DocCompanyId = docCompanyId,
            DocCompanyName = docCompanyName,
        });
    }

    /// <summary>The tenant's latest changes, newest first.</summary>
    public static Task<List<IntegrationChange>> LatestAsync(ScreenTailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.IntegrationChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.At)
            .Take(Shown)
            .ToListAsync(ct);
    }

    /// <summary>The latest connect or replace of each provider, for "Connected by" on its card.</summary>
    public static async Task<Dictionary<string, IntegrationChange>> LastConnectedAsync(ScreenTailContext db, Guid tenantId, IEnumerable<string> providers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(providers);
        var found = new Dictionary<string, IntegrationChange>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            var last = await db.IntegrationChanges.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Provider == provider && (c.Action == Connected || c.Action == Replaced))
                .OrderByDescending(c => c.At)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (last is not null)
            {
                found[provider] = last;
            }
        }

        return found;
    }

    /// <summary>
    /// Names for the people and devices behind some changes, read inside the tenant only. A name that can
    /// no longer be found is null, and the page says so in words.
    /// </summary>
    public static async Task<Func<IntegrationChange, ChangedBy>> NamesAsync(ScreenTailContext db, Guid tenantId, IReadOnlyCollection<IntegrationChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(changes);
        var ownerIds = changes.Where(c => c.OwnerId is not null).Select(c => c.OwnerId!.Value).Distinct().ToList();
        var userIds = changes.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).Distinct().ToList();
        var deviceIds = changes.Where(c => c.DeviceId is not null).Select(c => c.DeviceId!.Value).Distinct().ToList();

        var owners = ownerIds.Count == 0
            ? []
            : await db.Owners.AsNoTracking().Where(o => o.TenantId == tenantId && ownerIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.DisplayName, ct).ConfigureAwait(false);
        var users = userIds.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct).ConfigureAwait(false);
        var devices = deviceIds.Count == 0
            ? []
            : await db.Devices.AsNoTracking().Where(d => d.TenantId == tenantId && deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct).ConfigureAwait(false);

        return change => change.OwnerId is { } owner
            ? new ChangedBy("owner", owners.GetValueOrDefault(owner), null)
            : new ChangedBy(
                "technician",
                change.UserId is { } user ? users.GetValueOrDefault(user) : null,
                change.DeviceId is { } device ? devices.GetValueOrDefault(device) : null);
    }
}
