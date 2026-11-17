using Microsoft.EntityFrameworkCore;
using ScreenTrail.Api.Data;

namespace ScreenTrail.Api.Vault;

/// <summary>Who made a change: the owner from the web admin, or a technician from one of their devices.</summary>
public sealed record ChangeActor(Guid? OwnerId, Guid? UserId, Guid? DeviceId)
{
    public static ChangeActor Owner(Guid ownerId) => new(ownerId, null, null);

    public static ChangeActor Technician(Guid userId, Guid deviceId) => new(null, userId, deviceId);
}

/// <param name="Kind"><c>owner</c>, <c>technician</c>, or <c>screentrail</c> for what the service did itself (removing old records).</param>
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
    public const int Shown = 100;

    /// <summary>Adds the change to the context; the caller's save writes it with the change it records.</summary>
    public static void Record(
        ScreenTrailContext db,
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

    /// <summary>The one row that says how many older mapping changes were removed, and when last.</summary>
    public const string MappingsPruned = "mappings_pruned";

    /// <summary>How long a map or unmap record is kept once a later one for the same company explains the current mapping.</summary>
    public static readonly TimeSpan MappingsKeptFor = TimeSpan.FromDays(180);

    /// <summary>A device past its mapping changes for a window (<see cref="MappingChangeLimit"/>): one row per device per window, so the owner sees a flood and can revoke the device.</summary>
    public const string DeviceSlowed = "device_slowed";

    /// <summary>Adds the owner-visible row for a device refused by the mapping limit. The caller saves.</summary>
    public static void RecordSlowed(ScreenTrailContext db, Guid tenantId, ChangeActor device, DateTimeOffset at) =>
        Record(db, tenantId, "hudu", DeviceSlowed, device, at);

    /// <summary>
    /// Records a technician's map or unmap, unless the tenant's latest change is this same one (the same
    /// person and device, the same companies): a device that sends the same mapping over and over adds
    /// one row, not one per request. Then removes old mapping records (<see cref="PruneAsync"/>). The
    /// caller saves.
    /// </summary>
    public static async Task RecordMappingAsync(
        ScreenTrailContext db,
        Guid tenantId,
        string provider,
        string action,
        ChangeActor by,
        DateTimeOffset at,
        string? psaCompany,
        string? docCompanyId = null,
        string? docCompanyName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(by);
        var latest = await db.IntegrationChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.At)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var repeat = latest is not null
            && latest.Provider == provider
            && latest.Action == action
            && latest.OwnerId == by.OwnerId
            && latest.UserId == by.UserId
            && latest.DeviceId == by.DeviceId
            && latest.PsaCompany == psaCompany
            && latest.DocCompanyId == docCompanyId
            && latest.DocCompanyName == docCompanyName;
        if (repeat)
        {
            // The same choice again: the one row is moved to now, so it stays no older than the mapping
            // it chose (CurrentMappingsAsync names the person only then).
            _ = await db.IntegrationChanges.Where(c => c.Id == latest!.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.At, at), ct).ConfigureAwait(false);
        }
        else
        {
            Record(db, tenantId, provider, action, by, at, psaCompany, docCompanyId, docCompanyName);
        }

        _ = await PruneAsync(db, tenantId, at, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes, by age only, map and unmap records older than <see cref="MappingsKeptFor"/>, and
    /// <see cref="DeviceSlowed"/> rows that old. Nothing younger is ever removed, whatever the traffic.
    /// Never removed: the latest record for a PSA company that has a current mapping (it explains that
    /// mapping, however old), and any credential change. A company with no current mapping keeps nothing
    /// past that age, so the record is bounded by the per-device limits over <see cref="MappingsKeptFor"/>.
    /// What was removed is counted on one <see cref="MappingsPruned"/> row the owner sees. The caller saves it.
    /// </summary>
    /// <returns>How many records were removed.</returns>
    public static async Task<int> PruneAsync(ScreenTrailContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var before = now - MappingsKeptFor;
        var removed = await db.IntegrationChanges
            .Where(c => c.TenantId == tenantId && (c.Action == Mapped || c.Action == Unmapped) && c.At < before)
            .Where(c =>
                db.IntegrationChanges.Any(later =>
                    later.TenantId == c.TenantId
                    && (later.Action == Mapped || later.Action == Unmapped)
                    && later.PsaCompany == c.PsaCompany
                    && later.At > c.At)
                || !db.CompanyMappings.Any(m => m.TenantId == c.TenantId && m.PsaCompany == c.PsaCompany))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        removed += await db.IntegrationChanges
            .Where(c => c.TenantId == tenantId && c.Action == DeviceSlowed && c.At < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (removed == 0)
        {
            return 0;
        }

        await CountPrunedAsync(db, tenantId, removed, now, ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Adds to the tenant's one summary row (a unique index says one), counted up in the database itself so
    /// two prunes at once both add. The first prune inserts it; one that loses that race finds the row the
    /// other made and adds to it. Inside the caller's transaction EF rolls the failed insert back to its
    /// own savepoint, so the transaction goes on. This saves the context.
    /// </summary>
    internal static async Task CountPrunedAsync(ScreenTrailContext db, Guid tenantId, int removed, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await AddToSummaryAsync(db, tenantId, removed, now, ct).ConfigureAwait(false))
        {
            return;
        }

        var summary = new IntegrationChange { Id = Guid.NewGuid(), TenantId = tenantId, At = now, Provider = "hudu", Action = MappingsPruned, Removed = removed };
        _ = db.IntegrationChanges.Add(summary);
        try
        {
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.Entry(summary).State = EntityState.Detached;
            if (!await AddToSummaryAsync(db, tenantId, removed, now, ct).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private static async Task<bool> AddToSummaryAsync(ScreenTrailContext db, Guid tenantId, int removed, DateTimeOffset now, CancellationToken ct) =>
        await db.IntegrationChanges
            .Where(c => c.TenantId == tenantId && c.Action == MappingsPruned)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Removed, c => (c.Removed ?? 0) + removed).SetProperty(c => c.At, now), ct)
            .ConfigureAwait(false) > 0;

    /// <summary>The tenant's latest changes, newest first.</summary>
    public static Task<List<IntegrationChange>> LatestAsync(ScreenTrailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.IntegrationChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.At)
            .Take(Shown)
            .ToListAsync(ct);
    }

    /// <summary>A current mapping and the record of the person who chose it, if one did.</summary>
    public sealed record CurrentMapping(CompanyMapping Mapping, IntegrationChange? SetBy);

    /// <summary>
    /// Every company mapping as it stands, by PSA company name, each with the record of the person who chose
    /// it. A record names the person only when the mapping was made by hand, the record is no older than
    /// the mapping, and it comes after the tenant's latest clearing of mappings; otherwise the mapping is
    /// shown as matched by name (ScreenTrail matching it while publishing is not recorded), never as the
    /// choice of someone whose mapping was since cleared.
    /// </summary>
    public static async Task<List<CurrentMapping>> CurrentMappingsAsync(ScreenTrailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var mappings = await db.CompanyMappings.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.PsaCompany)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var latest = await db.IntegrationChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Action == Mapped)
            .Where(c => !db.IntegrationChanges.Any(later =>
                later.TenantId == c.TenantId
                && (later.Action == Mapped || later.Action == Unmapped)
                && later.PsaCompany == c.PsaCompany
                && later.At > c.At))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var cleared = await db.IntegrationChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Action == MappingsCleared)
            .MaxAsync(c => (DateTimeOffset?)c.At, ct)
            .ConfigureAwait(false);
        var byCompany = latest.GroupBy(c => c.PsaCompany ?? string.Empty, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. mappings.Select(m => new CurrentMapping(
            m,
            byCompany.TryGetValue(m.PsaCompany, out var record)
                && m.Confidence == "manual"
                && record.DocCompanyId == m.DocCompanyId
                && record.At >= m.CreatedAt
                && (cleared is not { } clearedAt || record.At > clearedAt)
                ? record
                : null))];
    }

    /// <summary>The latest connect or replace of each provider, for "Connected by" on its card.</summary>
    public static async Task<Dictionary<string, IntegrationChange>> LastConnectedAsync(ScreenTrailContext db, Guid tenantId, IEnumerable<string> providers, CancellationToken ct = default)
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
    public static async Task<Func<IntegrationChange, ChangedBy>> NamesAsync(ScreenTrailContext db, Guid tenantId, IReadOnlyCollection<IntegrationChange> changes, CancellationToken ct = default)
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
            : change.UserId is null && change.DeviceId is null
            ? new ChangedBy("screentail", "ScreenTrail", null)
            : new ChangedBy(
                "technician",
                change.UserId is { } user ? users.GetValueOrDefault(user) : null,
                change.DeviceId is { } device ? devices.GetValueOrDefault(device) : null);
    }
}
