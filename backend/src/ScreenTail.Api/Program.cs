using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;
using ScreenTail.Api.Vault;

var builder = WebApplication.CreateBuilder(args);

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

// ST-010 has no enrolment flow yet, so a client has nothing to put in an Authorization header and the
// drafting path can be tested but not run. This prints a token for one seeded device and exits without
// ever listening, and DevEnrolment refuses outright unless this is Development -- starting the
// production container with the flag still on a command line is an accident somebody will have.
//
// Delete this with ST-010.
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

var v1 = app.MapGroup("/v1").RequireAuthorization();
_ = v1.MapMe();
_ = v1.MapSummarize();
_ = v1.MapIntegrations();
_ = v1.MapPsa();
_ = v1.MapPublish();

await app.RunAsync();

internal sealed record HealthResponse(string Status);

/// <summary>Visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
