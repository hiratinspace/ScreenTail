using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Logging;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;
using ScreenTail.Api.Tenancy;
using ScreenTail.Api.Vault;

var builder = WebApplication.CreateBuilder(args);

// ST-011: one log sink, scrubbed (INV-10), in place of the framework's providers. The level comes from
// the usual Logging:LogLevel:Default setting and is Information unless said otherwise.
_ = builder.Logging.ClearProviders();
_ = builder.Logging.AddProvider(new ScrubbingLoggerProvider(
    Console.Error,
    Enum.TryParse<LogLevel>(builder.Configuration["Logging:LogLevel:Default"], ignoreCase: true, out var minimum) && minimum != LogLevel.None ? minimum : LogLevel.Information));

// ST-008. The signing key has no default and the service refuses to start without one: a development
// default becomes a production key the first time somebody forgets to set it, and the failure is silent.
var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddSingleton(TimeProvider.System);

// Postgres in production. Tests replace this registration with SQLite over the same model, so the model
// and every query are exercised on any machine while CI applies the real migrations to a real Postgres.
//
// Missing is fatal, and at startup rather than at the first request. Without the registration the
// endpoints still map and then fail with "body was inferred but the method does not allow inferred body
// parameters", which says nothing about the connection string somebody forgot to set.
var connection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres must be set. The service talks to one database and cannot serve any "
        + "authenticated request without it.");

builder.Services.AddDbContext<ScreenTailContext>(options => options.UseNpgsql(connection));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = TokenIssuer.Validation(jwt);

        // No token details in the response. A 401 that explains which check failed is a 401 that helps
        // whoever is guessing (INV-10 in spirit: say nothing an attacker can use, and nothing a customer
        // would mind reading).
        options.IncludeErrorDetails = false;
        options.MapInboundClaims = false;
    });

// ST-063. The key has no default and is read from the environment; with none set the service still
// starts and says drafting is not configured, which is true and actionable rather than a dead deployment.
var summarization = builder.Configuration.GetSection(SummarizationOptions.Section).Get<SummarizationOptions>()
    ?? new SummarizationOptions();
builder.Services.AddSingleton(summarization);
builder.Services.AddScoped<ICostLedger, CostLedger>();

var prompt = PromptLibrary.Note();
builder.Services.AddHttpClient(nameof(GeminiProvider), client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = summarization.Timeout;
});

// The client is asked for by name. Handing this to ActivatorUtilities looked the same and was not: it
// resolves a plain HttpClient from the container, which is the unnamed one — no base address, so the
// provider's relative URL threw before anything was sent, and the default timeout rather than ours.
// Drafting could not have worked on any deployment (2026-09-20 review).
builder.Services.AddScoped<SummarizationService>(services => new SummarizationService(
    new GeminiProvider(
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GeminiProvider)),
        summarization,
        prompt),
    fallback: null,
    services.GetRequiredService<ICostLedger>(),
    summarization));

// ST-009. The vault's master key has no default either: without one the service starts, lists what it
// has, and refuses to store a credential with a reason. A development default would protect every
// tenant's PSA key with a string in the repository.
var vault = builder.Configuration.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions();
builder.Services.AddSingleton(vault);
builder.Services.AddScoped<IIntegrationVault, IntegrationVault>();

// ST-091. The PSA is built per tenant from the vault, never resolved as one provider for everybody:
// a provider without a tenant's credential would be a fake by definition. The named client carries the
// timeout; the factory gives each one the tenant's own base address.
var connectWise = builder.Configuration.GetSection(ConnectWiseOptions.Section).Get<ConnectWiseOptions>() ?? new ConnectWiseOptions();
builder.Services.AddSingleton(connectWise);
builder.Services.AddHttpClient(nameof(ConnectWiseProvider), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IPsaProviderFactory, ConnectWiseProviderFactory>();

// ST-095. The documentation platform, the same way: per tenant from the vault, never as one provider
// for everybody. The company cache is the deployment's, keyed by tenant, ten minutes.
var hudu = builder.Configuration.GetSection(HuduOptions.Section).Get<HuduOptions>() ?? new HuduOptions();
builder.Services.AddSingleton(hudu);
builder.Services.AddSingleton<HuduCompanyCache>();
builder.Services.AddHttpClient(nameof(HuduProvider), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IDocProviderFactory, HuduProviderFactory>();

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

// A request that has not finished in this long has stopped being a request and started being a held
// connection. AddRequestTimeouts alone does nothing — the middleware below is what applies it, and
// without that line this was a registration with no effect (2026-09-19 review).
builder.Services.AddRequestTimeouts(timeouts =>
{
    // Longer than the drafting deadline, which is 60 s and is enforced by the summarisation service
    // itself with a message a technician can act on. This is the backstop for everything else.
    timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(90) };
});

var app = builder.Build();

// ST-010: the operator's side of tenancy, until there is a web admin (ST-099) and a mail provider.
// `--invite` issues a code for a tenant (or a new one) and prints it once; `--offboard-tenant` deletes
// every row of a tenant. Both exit without listening.
if (Array.IndexOf(args, "--invite") is var inviteAt and >= 0)
{
    // --invite <tenant-id | new:Name:seats> <email> [display name]
    var target = args.ElementAtOrDefault(inviteAt + 1) ?? throw new InvalidOperationException("--invite <tenant-id | new:Name:seats> <email> [display name]");
    var email = args.ElementAtOrDefault(inviteAt + 2) ?? throw new InvalidOperationException("--invite needs the technician's email after the tenant.");
    var displayName = args.ElementAtOrDefault(inviteAt + 3) ?? email;
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
    var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    Guid tenantId;
    if (target.StartsWith("new:", StringComparison.Ordinal))
    {
        var parts = target.Split(':', 3);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = parts.ElementAtOrDefault(1) is { Length: > 0 } n ? n : "New tenant", Seats = int.TryParse(parts.ElementAtOrDefault(2), out var seats) ? seats : 5, CreatedAt = time.GetUtcNow() };
        db.Tenants.Add(tenant);
        _ = await db.SaveChangesAsync();
        tenantId = tenant.Id;
        Console.WriteLine($"tenant  {tenant.Id}  {tenant.Name}  {tenant.Seats} seats");
    }
    else
    {
        tenantId = Guid.Parse(target);
    }

    var issued = await Invites.CreateAsync(db, tenantId, email, displayName, time);
    Console.WriteLine($"invite  {issued.Id}  for {email}  expires {issued.ExpiresAt:u}");
    Console.WriteLine();
    Console.WriteLine($"code    {issued.Code}");
    return;
}

// ST-047: --set-policy <tenant-id> [retention=<days>] [local-only=true|false] [locked=true|false] [all-windows=true|false]
// A new policy row each time; devices pick it up within the hour.
if (Array.IndexOf(args, "--set-policy") is var policyAt and >= 0)
{
    var tenantId = Guid.Parse(args.ElementAtOrDefault(policyAt + 1) ?? throw new InvalidOperationException("--set-policy <tenant-id> [retention=7] [local-only=false] [locked=false] [all-windows=false]"));
    var settings = args.Skip(policyAt + 2).TakeWhile(a => a.Contains('=', StringComparison.Ordinal)).Select(a => a.Split('=', 2)).ToDictionary(kv => kv[0].ToLowerInvariant(), kv => kv[1], StringComparer.Ordinal);
    await using var scope = app.Services.CreateAsyncScope();
    var set = await Policies.SetAsync(
        scope.ServiceProvider.GetRequiredService<ScreenTailContext>(),
        tenantId,
        settings.TryGetValue("retention", out var r) && int.TryParse(r, out var days) ? days : 7,
        settings.TryGetValue("local-only", out var lo) && bool.Parse(lo),
        settings.TryGetValue("locked", out var lk) && bool.Parse(lk),
        settings.TryGetValue("all-windows", out var aw) && bool.Parse(aw),
        scope.ServiceProvider.GetRequiredService<TimeProvider>());
    Console.WriteLine($"policy  {set.Version}  retention {set.RetentionDays} d  local-only {set.LocalOnly} (locked {set.LocalOnlyLocked})  all-windows {set.CaptureAllWindows}");
    return;
}

if (Array.IndexOf(args, "--offboard-tenant") is var offboardAt and >= 0)
{
    var tenantId = Guid.Parse(args.ElementAtOrDefault(offboardAt + 1) ?? throw new InvalidOperationException("--offboard-tenant <tenant-id>"));
    await using var scope = app.Services.CreateAsyncScope();
    var gone = await Offboarding.DeleteTenantAsync(scope.ServiceProvider.GetRequiredService<ScreenTailContext>(), tenantId);
    Console.WriteLine($"offboarded {gone.TenantId}: {gone.Rows} row(s) deleted across every table");
    return;
}

// A development shortcut from before ST-010 had an activation flow: one seeded device, its access token
// printed. Kept because the M1 runbook uses it; DevEnrolment refuses outright unless this is Development.
if (args.Contains("--enrol-dev-device", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var enrolled = await DevEnrolment.EnrolAsync(
        scope.ServiceProvider.GetRequiredService<ScreenTailContext>(),
        scope.ServiceProvider.GetRequiredService<TokenIssuer>(),
        app.Environment.IsDevelopment());

    Console.WriteLine($"tenant  {enrolled.TenantId}");
    Console.WriteLine($"device  {enrolled.DeviceId}");
    Console.WriteLine($"expires {enrolled.ExpiresAt:u}");
    Console.WriteLine();
    Console.WriteLine($"SCREENTAIL_DEVICE_TOKEN={enrolled.Token}");
    return;
}

// ST-009. Rewraps every credential still under Vault:PreviousMasterKey and exits without listening.
// docs/security/key-rotation.md says when to run it and what to do after.
if (args.Contains("--rotate-vault-keys", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var rotated = await KeyRotation.RotateAsync(
        scope.ServiceProvider.GetRequiredService<ScreenTailContext>(),
        scope.ServiceProvider.GetRequiredService<VaultOptions>(),
        scope.ServiceProvider.GetRequiredService<TimeProvider>());
    Console.WriteLine($"rotated {rotated} credential(s) to master key {Envelope.KeyIdOf(vault.CurrentKey())}");
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRequestTimeouts();
app.UseAuthentication();
app.UseAuthorization();

// Liveness only: checks no dependencies and returns no data (INV-7, INV-10).
app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")));

// Development only. The document lists every endpoint, every field of the bundle and every error shape,
// which is a map of the service for anyone who asks — and it was served unauthenticated in every
// environment, including production (2026-09-19 review). A deployment that wants it published can put it
// behind whatever its operators already use to publish documentation.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/swagger/v1/swagger.json");
}

// The two calls a device makes before it has a token (ST-010). Anonymous, and outside the group below.
_ = app.MapGroup("/v1/devices").AllowAnonymous().MapDevices();

var v1 = app.MapGroup("/v1").RequireAuthorization();
_ = v1.MapMe();
_ = v1.MapSummarize();
_ = v1.MapIntegrations();
_ = v1.MapPsa();
_ = v1.MapPublish();
_ = v1.MapCompanyMappings();
_ = v1.MapPolicy();

await app.RunAsync();

internal sealed record HealthResponse(string Status);

/// <summary>Visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
