using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Endpoints;

public sealed record SeatUsage(int Used, int Total);

public sealed record AdminDevice(Guid Id, string Name, DateTimeOffset ActivatedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt);

public sealed record AdminTechnician(Guid Id, string Email, string DisplayName, DateTimeOffset? DisabledAt, IReadOnlyList<AdminDevice> Devices);

/// <param name="Status"><c>pending</c>, <c>accepted</c> or <c>expired</c>.</param>
public sealed record AdminInvite(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, string Status);

public sealed record TechniciansResponse(SeatUsage Seats, IReadOnlyList<AdminTechnician> Technicians, IReadOnlyList<AdminInvite> Invites);

public sealed record CreateInviteRequest(string? Email, string? DisplayName);

/// <param name="Code">The one copy in the clear. Shown to the owner once; the database keeps its hash.</param>
public sealed record CreatedInvite(Guid Id, string Email, string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// A whole policy, every field required. Nullable so that a field left out is refused rather than read
/// as false: a page that forgot <c>localOnlyLocked</c> must not unlock it (INV-11).
/// </summary>
/// <param name="ConfirmAllWindows">INV-5: capture outside the remote tools is opt-in and confirmed.</param>
public sealed record UpdatePolicyRequest(
    int? RetentionDays,
    bool? LocalOnly,
    bool? LocalOnlyLocked,
    bool? CaptureAllWindows,
    bool? ConfirmAllWindows,
    string? UpdateChannel,
    bool? UpdateChannelLocked,
    int? UpdateCheckHours,
    bool? UpdateChecksUnderLocalOnly);

/// <param name="LastResult"><c>ok</c>, <c>failed</c>, or null before the first test.</param>
public sealed record AdminIntegration(string Provider, string Name, bool Connected, string? SiteUrl, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastResult, string? LastError);

public sealed record AdminIntegrationsResponse(IReadOnlyList<AdminIntegration> Integrations);

public sealed record DownloadResponse(string? Url, string Message);

/// <summary>
/// The owner's routes (ST-099), all behind <see cref="OwnerPolicy"/>. Every query is filtered by the
/// session's tenant and by nothing a request can say: an id in a path is looked up inside that tenant,
/// and one from another tenant is a 404 like one that does not exist.
/// </summary>
public static class AdminEndpoint
{
    public const int RetentionMin = 1;
    public const int RetentionMax = 365;
    public const int UpdateHoursMax = 168;

    public static RouteGroupBuilder MapAdmin(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/session", (AdminCaller owner) => Results.Ok(AdminSessionResponse.For(owner)))
            .WithName("AdminSession")
            .WithSummary("The signed-in owner, their tenant, and the session's CSRF value.");

        group.MapGet("/technicians", async (AdminCaller owner, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking()
                .Where(u => u.TenantId == owner.TenantId)
                .OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.Email, u.DisplayName, u.DisabledAt })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var devices = await db.Devices.AsNoTracking()
                .Where(d => d.TenantId == owner.TenantId)
                .Select(d => new { d.UserId, Row = new AdminDevice(d.Id, d.Name, d.ActivatedAt, d.LastSeenAt, d.RevokedAt) })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var invites = await db.Invites.AsNoTracking()
                .Where(i => i.TenantId == owner.TenantId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var now = time.GetUtcNow();
            var byUser = devices.ToLookup(d => d.UserId, d => d.Row);
            return Results.Ok(new TechniciansResponse(
                new SeatUsage(devices.Count(d => d.Row.RevokedAt is null), owner.Tenant.Seats),
                [.. users.Select(u => new AdminTechnician(u.Id, u.Email, u.DisplayName, u.DisabledAt, [.. byUser[u.Id].OrderBy(d => d.ActivatedAt)]))],
                [.. invites
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => new AdminInvite(i.Id, i.Email, i.DisplayName, i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.AcceptedAt is not null ? "accepted" : i.ExpiresAt <= now ? "expired" : "pending"))]));
        })
        .WithName("AdminTechnicians")
        .WithSummary("The tenant's technicians, their devices, the seats in use and the invites issued.");

        group.MapPost("/invites", async (CreateInviteRequest request, AdminCaller owner, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant();
            if (!LooksLikeAnAddress(email))
            {
                return Results.BadRequest(new { error = "bad_email", message = "Enter the technician's work email address." });
            }

            var name = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim();
            if (name.Length > 200)
            {
                return Results.BadRequest(new { error = "bad_display_name", message = "The name can be up to 200 characters." });
            }

            // The same code, hash and 72 hours as the operator's --invite. No mail provider yet, so the
            // code goes back to the owner, once, to hand on (ADR-0009).
            var issued = await Invites.CreateAsync(db, owner.TenantId, email, name, time, ct).ConfigureAwait(false);
            return Results.Json(new CreatedInvite(issued.Id, email, issued.Code, issued.ExpiresAt), statusCode: StatusCodes.Status201Created);
        })
        .WithName("AdminCreateInvite")
        .WithSummary("Issues a 72-hour, one-device invite code for a technician, returned once.");

        group.MapPost("/devices/{id:guid}/revoke", async (Guid id, AdminCaller owner, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var revoked = await db.Devices
                .Where(d => d.Id == id && d.TenantId == owner.TenantId && d.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, now), ct)
                .ConfigureAwait(false);
            if (revoked > 0)
            {
                return Results.NoContent();
            }

            // Already revoked is done; another tenant's device is the same as no device.
            return await db.Devices.AnyAsync(d => d.Id == id && d.TenantId == owner.TenantId, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "device_unknown", message = "There is no such device in your organisation." });
        })
        .WithName("AdminRevokeDevice")
        .WithSummary("Revokes one of the tenant's devices. Its token stops working on its next request.");

        group.MapGet("/policy", async (AdminCaller owner, ScreenTailContext db, CancellationToken ct) =>
            Results.Ok(PolicyEndpoint.Describe(await Policies.LatestAsync(db, owner.TenantId, ct).ConfigureAwait(false))))
        .WithName("AdminGetPolicy")
        .WithSummary("The tenant's policy, exactly as devices receive it.");

        group.MapPut("/policy", async (UpdatePolicyRequest request, AdminCaller owner, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (request is not
                {
                    RetentionDays: { } retention,
                    LocalOnly: { } localOnly,
                    LocalOnlyLocked: { } localOnlyLocked,
                    CaptureAllWindows: { } allWindows,
                    UpdateChannel: { } channel,
                    UpdateChannelLocked: { } channelLocked,
                    UpdateCheckHours: { } hours,
                    UpdateChecksUnderLocalOnly: { } underLocalOnly,
                })
            {
                return Results.BadRequest(new { error = "incomplete_policy", message = "Send every policy field. A field left out is not read as off." });
            }

            if (retention is < RetentionMin or > RetentionMax)
            {
                return Results.BadRequest(new { error = "bad_retention", message = $"Keep raw data for {RetentionMin} to {RetentionMax} days." });
            }

            if (channel is not ("stable" or "pilot"))
            {
                return Results.BadRequest(new { error = "bad_update_channel", message = "The update channel is stable or pilot." });
            }

            if (hours is < 0 or > UpdateHoursMax)
            {
                return Results.BadRequest(new { error = "bad_update_hours", message = $"Check for updates every 1 to {UpdateHoursMax} hours, or 0 for never." });
            }

            // INV-5: every window, not just the remote tools, is an opt-in somebody confirmed. Turning it
            // on needs the confirmation; leaving it on, once it is on, does not ask again.
            var current = await Policies.LatestAsync(db, owner.TenantId, ct).ConfigureAwait(false);
            if (allWindows && current?.CaptureAllWindows != true && request.ConfirmAllWindows != true)
            {
                return Results.BadRequest(new { error = "all_windows_unconfirmed", message = "Capturing every window needs confirming. Tick the confirmation and save again." });
            }

            var saved = await Policies.SetAsync(db, owner.TenantId, retention, localOnly, localOnlyLocked, allWindows, new Policies.UpdateSettings(channel, channelLocked, hours, underLocalOnly), time, ct).ConfigureAwait(false);
            return Results.Ok(PolicyEndpoint.Describe(saved));
        })
        .WithName("AdminSetPolicy")
        .WithSummary("Sets the tenant's policy as a new version. Devices pick it up within the hour.");

        group.MapGet("/integrations", async (AdminCaller owner, ScreenTailContext db, CancellationToken ct) =>
        {
            // Never a credential, not even its last four: the owner sees whether it is there and whether
            // it worked, and technicians manage it from Settings.
            var rows = await db.Integrations.AsNoTracking()
                .Where(i => i.TenantId == owner.TenantId && (i.Provider == "connectwise" || i.Provider == "hudu"))
                .Select(i => new { i.Provider, i.SiteUrl, i.ConnectedAt, i.LastCheckedAt, i.LastError })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(new AdminIntegrationsResponse([.. Known.Select(k =>
            {
                var row = rows.SingleOrDefault(r => r.Provider == k.Provider);
                return row is null
                    ? new AdminIntegration(k.Provider, k.Name, false, null, null, null, null, null)
                    : new AdminIntegration(k.Provider, k.Name, true, row.SiteUrl, row.ConnectedAt, row.LastCheckedAt, row.LastCheckedAt is null ? null : row.LastError is null ? "ok" : "failed", row.LastError);
            })]));
        })
        .WithName("AdminIntegrations")
        .WithSummary("Whether ConnectWise and Hudu are connected and how their last test went. No credentials.");

        group.MapGet("/download", (AdminOptions options) =>
        {
            var url = options.DownloadUri();
            return Results.Ok(url is null
                ? new DownloadResponse(null, "No client download is published for this deployment yet. Ask your ScreenTail contact for the installer.")
                : new DownloadResponse(url.ToString(), "Install ScreenTail on each technician's Windows PC, then activate it with their invite code."));
        })
        .WithName("AdminDownload")
        .WithSummary("Where technicians download the Windows client, when the deployment has one.");

        return group;
    }

    private static readonly (string Provider, string Name)[] Known = [("connectwise", "ConnectWise Manage"), ("hudu", "Hudu")];

    private static bool LooksLikeAnAddress([NotNullWhen(true)] string? email) =>
        email is { Length: >= 3 and <= 320 }
        && email.IndexOf('@', StringComparison.Ordinal) is var at and > 0
        && at < email.Length - 1
        && email.IndexOf('@', at + 1) < 0
        && !email.Any(char.IsWhiteSpace);
}
