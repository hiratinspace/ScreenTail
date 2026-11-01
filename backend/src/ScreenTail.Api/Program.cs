using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Hosting;
using ScreenTail.Api.Logging;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Demo;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;
using ScreenTail.Api.Tenancy;
using ScreenTail.Api.Vault;

// ST-007. The container's health check: this program run again, asking the running one for /health.
// First, so it builds no host and reads no configuration or secret.
if (args is [HealthProbe.Argument])
{
    Environment.ExitCode = await HealthProbe.RunAsync(HealthProbe.Target(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")), HealthProbe.Timeout);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ST-007. A deployment's secrets as one file per key (Docker secrets), kept out of the environment.
builder.Configuration.AddSecretFiles(Environment.GetEnvironmentVariable(SecretFiles.DirectoryVariable));

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

// ST-007. `--migrate` applies the migrations and exits. The service never migrates at startup: it is a
// deliberate step of a deployment, run with the role that owns the schema (DatabaseMigrator). Here,
// before any other setting is checked, so that step is given the connection string and nothing else.
if (args is [DatabaseMigrator.Argument])
{
    var applied = await DatabaseMigrator.ApplyAsync(connection, CancellationToken.None);
    Console.WriteLine(applied.Count == 0
        ? "migrations: the database is up to date"
        : $"migrations: applied {applied.Count}: {string.Join(", ", applied)}");
    return;
}

builder.Services.AddDbContext<ScreenTailContext>(options => options.UseNpgsql(connection));

// Built here rather than inside AddJwtBearer, whose callback runs at the first authenticated request: a
// host with no key used to start, answer /health and fail only when a token arrived (2026-10-12 review).
var tokenValidation = TokenIssuer.Validation(jwt);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = tokenValidation;

        // No token details in the response. A 401 that explains which check failed is a 401 that helps
        // whoever is guessing (INV-10 in spirit: say nothing an attacker can use, and nothing a customer
        // would mind reading).
        options.IncludeErrorDetails = false;
        options.MapInboundClaims = false;
    })

    // ST-099. The web admin's session: the same key, its own audience, read from a cookie and never
    // from the Authorization header (ADR-0009). Admin routes authenticate with this scheme alone.
    .AddAdminSession(jwt);

// ST-099. Owner sign-in with Google. No client id, no sign-in: the endpoints answer 501 and nothing else
// changes. No client secret exists, because checking an ID token needs only Google's public keys. An
// empty signup list means nobody can create a tenant (ADR-0009 lists these as the owner's decisions).
var adminOptions = builder.Configuration.GetSection(AdminOptions.Section).Get<AdminOptions>() ?? new AdminOptions();
adminOptions.Validate(jwt);
builder.Services.AddSingleton(adminOptions);
builder.Services.AddHttpClient(GoogleSigningKeys.HttpClientName, client => client.Timeout = GoogleSigningKeys.FetchTimeout);
builder.Services.AddSingleton<IGoogleSigningKeys>(services => new GoogleSigningKeys(
    () => services.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleSigningKeys.HttpClientName),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<GoogleSigningKeys>>()));
builder.Services.AddSingleton<GoogleIdTokenVerifier>();
builder.Services.AddSingleton<SignInNonces>();
builder.Services.AddOwnerPolicy();

// ST-099 hardening. The anonymous routes (the admin's sign-in nonce, sign-in and signup, a device's
// activation) are counted per client address and the call past the limit is refused with 429 before
// anything checks it. Sign-out is not counted (ADR-0009).
var rateLimits = builder.Configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
rateLimits.Validate();
builder.Services.AddSingleton(rateLimits);
builder.Services.AddAnonymousRateLimit(rateLimits);

// ST-010 follow-up (ADR-0009 decision 16). Activations refused for their code are counted across every
// address, and the operator is warned, in a line with two numbers in it, when they pass a threshold.
// Nobody is refused or slowed for it: the invite code's 99 bits are what stop guessing.
var activationWatch = builder.Configuration.GetSection(ActivationWatchOptions.Section).Get<ActivationWatchOptions>() ?? new ActivationWatchOptions();
activationWatch.Validate();
builder.Services.AddSingleton(activationWatch);
builder.Services.AddSingleton<ActivationWatch>();

// Behind a reverse proxy every caller would arrive from the proxy and share one count, so one person
// could lock everyone out of sign-in. The operator lists the proxies (ForwardedHeaders:KnownProxies,
// ForwardedHeaders:KnownNetworks); only a connection from one of them has its X-Forwarded-For read.
// Nothing is listed by default, and then no forwarded header is read from anybody.
var trustedProxies = builder.Configuration.GetSection(TrustedProxyOptions.Section).Get<TrustedProxyOptions>() ?? new TrustedProxyOptions();
trustedProxies.Validate(builder.Configuration[TrustedProxyOptions.FrameworkSwitch]);
builder.Services.PostConfigure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(trustedProxies.Apply);

// ST-063. The key has no default and is read from the environment; with none set the service still
// starts and says drafting is not configured, which is true and actionable rather than a dead deployment.
var summarization = builder.Configuration.GetSection(SummarizationOptions.Section).Get<SummarizationOptions>()
    ?? new SummarizationOptions();

// An unknown provider stops the service here: the key would otherwise go to a company it was not for.
// ST-013. Summarization:Provider=scripted drafts without a model, for the Windows end-to-end test and an
// offline rehearsal. Refused outside Development, like Providers:Demo below.
summarization.Validate(builder.Environment.IsDevelopment());

// Before any Anthropic client exists: the SDK adds every header ANTHROPIC_CUSTOM_HEADERS names to every
// request, reads it once per process, and offers no setting that stops it. ClaudeProvider also rewrites
// each request's headers, as a second layer.
ClaudeProvider.ForgetEnvironmentHeaders();
builder.Services.AddSingleton(summarization);
builder.Services.AddScoped<ICostLedger, CostLedger>();

// One fixed host per provider, and nothing else. The backend has no general egress list for drafting:
// each model is reached through a named client whose base address is set here and nowhere else, and the
// Claude provider pins the SDK to that address and that key, so neither ANTHROPIC_BASE_URL nor
// ANTHROPIC_API_KEY in the environment can redirect a customer's session.
var prompt = PromptLibrary.Note();
builder.Services.AddHttpClient(nameof(GeminiProvider), client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = summarization.Timeout;
});
builder.Services.AddHttpClient(nameof(ClaudeProvider), client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.Timeout = summarization.Timeout;
});

// The client is asked for by name. Handing this to ActivatorUtilities looked the same and was not: it
// resolves a plain HttpClient from the container, which is the unnamed one — no base address, so the
// provider's relative URL threw before anything was sent, and the default timeout rather than ours.
// Drafting could not have worked on any deployment (2026-09-20 review).
//
// Registered as itself, scoped, so the container disposes the Claude provider's SDK client with the
// request. Summarization:Provider picks which; Gemini is the default and unchanged. The scripted test
// provider (ST-013) is only reachable in Development, because Validate refused it everywhere else above.
builder.Services.AddScoped<ILlmProvider>(services =>
{
    if (summarization.Scripted)
    {
        return new ScriptedProvider();
    }

    var clients = services.GetRequiredService<IHttpClientFactory>();
    return summarization.IsClaude
        ? new ClaudeProvider(clients.CreateClient(nameof(ClaudeProvider)), summarization, prompt)
        : new GeminiProvider(clients.CreateClient(nameof(GeminiProvider)), summarization, prompt);
});
builder.Services.AddScoped<SummarizationService>(services => new SummarizationService(
    services.GetRequiredService<ILlmProvider>(),
    fallback: null,
    services.GetRequiredService<ICostLedger>(),
    summarization,
    services.GetRequiredService<TimeProvider>()));

// ST-009. The vault's master key has no default either: without one the service starts, lists what it
// has, and refuses to store a credential with a reason. A development default would protect every
// tenant's PSA key with a string in the repository.
var vault = builder.Configuration.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions();
builder.Services.AddSingleton(vault);
builder.Services.AddScoped<IIntegrationVault, IntegrationVault>();

// Security check 2026-10-14. A tenant chooses its provider's site, so the PSA and documentation clients
// connect only to public addresses, checked at connect time (PublicNetwork). The option that lifts this
// is for Development and tests; anywhere else the service refuses to start with it on.
var providerNetwork = builder.Configuration.GetSection(ProviderNetworkOptions.Section).Get<ProviderNetworkOptions>() ?? new ProviderNetworkOptions();
providerNetwork.Validate(builder.Environment.IsDevelopment());
builder.Services.AddSingleton(providerNetwork);

// ST-091. The PSA is built per tenant from the vault, never resolved as one provider for everybody:
// a provider without a tenant's credential would be a fake by definition. The named client carries the
// timeout; the factory gives each one the tenant's own base address.
var connectWise = builder.Configuration.GetSection(ConnectWiseOptions.Section).Get<ConnectWiseOptions>() ?? new ConnectWiseOptions();
builder.Services.AddSingleton(connectWise);
builder.Services.AddHttpClient(nameof(ConnectWiseProvider), ProviderHttp.Configure)
    .ConfigurePrimaryHttpMessageHandler(() => PublicNetwork.Handler(providerNetwork.AllowPrivateAddresses));
builder.Services.AddScoped<IPsaProviderFactory, ConnectWiseProviderFactory>();

// ST-095. The documentation platform, the same way: per tenant from the vault, never as one provider
// for everybody. The company cache is the deployment's, keyed by tenant, ten minutes.
var hudu = builder.Configuration.GetSection(HuduOptions.Section).Get<HuduOptions>() ?? new HuduOptions();
builder.Services.AddSingleton(hudu);
builder.Services.AddSingleton<HuduCompanyCache>();
builder.Services.AddHttpClient(nameof(HuduProvider), ProviderHttp.Configure)
    .ConfigurePrimaryHttpMessageHandler(() => PublicNetwork.Handler(providerNetwork.AllowPrivateAddresses));
builder.Services.AddScoped<IDocProviderFactory, HuduProviderFactory>();

// ST-090. A demo PSA and knowledge base in place of the tenant's, for showing a drafted note land on a
// ticket while there is no ConnectWise sandbox. Off unless Providers:Demo is on, and refused outside
// Development: a deployment with it on would say a note was published when it went nowhere anybody can
// read. docs/dev/demo-psa.md says how to switch it on and how to show the note.
var demo = builder.Configuration.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
demo.Validate(builder.Environment.IsDevelopment());
builder.Services.AddSingleton(demo);
if (demo.Demo)
{
    _ = builder.Services.AddDemoProviders();
}

// Revocation for every route under /v1, as a policy on the group rather than a line in each handler
// (2026-10-12 review). A refused caller gets the same 401 as a bad token, before its body is read.
builder.Services.AddCallerPolicy();
builder.Services.AddOpenApi();

// A request that has not finished in this long has stopped being a request and started being a held
// connection. AddRequestTimeouts alone does nothing — the middleware below is what applies it, and
// without that line this was a registration with no effect (2026-09-19 review).
builder.Services.AddRequestTimeouts(timeouts =>
{
    // Longer than the drafting deadline (60 s on Gemini, 85 s on Claude), which the summarisation
    // service enforces itself with a message a technician can act on. This is the backstop for
    // everything else, and stays thirty seconds clear of that deadline whatever it is set to.
    var backstop = summarization.Timeout + TimeSpan.FromSeconds(30);
    timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = backstop > TimeSpan.FromSeconds(90) ? backstop : TimeSpan.FromSeconds(90) };
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
// ST-113: [update-channel=stable|pilot] [update-channel-locked=true|false] [update-hours=0-168] [update-local-only=true|false]
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
        new Policies.UpdateSettings(
            settings.TryGetValue("update-channel", out var uc) ? uc : "stable",
            settings.TryGetValue("update-channel-locked", out var ucl) && bool.Parse(ucl),
            settings.TryGetValue("update-hours", out var uh) && int.TryParse(uh, out var hours) ? hours : 24,
            settings.TryGetValue("update-local-only", out var ulo) && bool.Parse(ulo)),
        scope.ServiceProvider.GetRequiredService<TimeProvider>());
    Console.WriteLine($"policy  {set.Version}  retention {set.RetentionDays} d  local-only {set.LocalOnly} (locked {set.LocalOnlyLocked})  all-windows {set.CaptureAllWindows}  updates {set.UpdateChannel} (locked {set.UpdateChannelLocked}) every {set.UpdateCheckHours} h, under local-only {set.UpdateChecksUnderLocalOnly}");
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

// ST-099 hardening. Every answer, refusals and errors included, says it is not to be rendered, framed,
// sniffed or followed by a referrer (ADR-0009).
app.UseSecurityHeaders();

// An exception nobody caught becomes a bare 500 here rather than in the server, which would drop the
// headers above with everything else it had set. The exception is still logged (scrubbed, INV-10), and
// nothing about it reaches the caller. In Development too, so the tests see what a deployment does.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = static _ => Task.CompletedTask,

    // A body the endpoint could not bind (malformed JSON, the wrong content type) is the caller's mistake
    // and keeps its 400 or 415. Development throws these rather than answering them, so without this the
    // demo laptop, which runs in Development, would answer them 500.
    StatusCodeSelector = static e => e is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});

// Before anything that reads the client's address or scheme (the rate limit's partition, HTTPS
// redirection), so each sees the client a listed proxy speaks for, and only then.
if (trustedProxies.Configured)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Before the timeout, so the publish deadline counts from no later than the timeout does (S-68).
app.UseRequestStart();
app.UseRequestTimeouts();
app.UseAdminNoStore();

// After the no-store guard, so a refusal is not kept either; before authentication, so a refused call
// costs nothing more than the count. A call another site's page started is refused before it is counted.
app.UseCrossSiteRefusal();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDraftingConcurrency();

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

// ST-090. What the demo PSA holds, for the audience to see the note on the ticket. Mapped only with the
// demo on, which is only ever Development, and answered only to this machine (DemoPsaEndpoint).
if (app.Environment.IsDevelopment() && demo.Demo)
{
    _ = app.MapDemoPsa();
}

// The two calls a device makes before it has a token (ST-010). Anonymous, and outside the group below.
_ = app.MapGroup("/v1/devices").AllowAnonymous().MapDevices();

// ST-099. The web admin: sign-in and signup are anonymous, everything else is an owner's, by cookie.
// Mapped beside the device group, not inside it, so no device policy applies here and no owner policy
// applies there.
_ = app.MapGroup("/v1/admin").AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(AdminSession.AnonymousBodyLimit)).MapAdminSession();
_ = app.MapGroup("/v1/admin").RequireAuthorization(OwnerPolicy.Name).MapAdmin();

var v1 = app.MapGroup("/v1").RequireAuthorization(CallerPolicy.Name);
_ = v1.MapMe();
_ = v1.MapSummarize();
_ = v1.MapIntegrations();
_ = v1.MapPsa();
_ = v1.MapPublish();
_ = v1.MapCompanyMappings();
_ = v1.MapPolicy();
_ = v1.MapMetrics();

await app.RunAsync();

internal sealed record HealthResponse(string Status);

/// <summary>Visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
