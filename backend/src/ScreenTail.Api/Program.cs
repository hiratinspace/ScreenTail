using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

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
builder.Services.AddHttpClient<GeminiProvider>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = summarization.Timeout;
});

builder.Services.AddScoped<SummarizationService>(services => new SummarizationService(
    ActivatorUtilities.CreateInstance<GeminiProvider>(services, summarization, prompt),
    fallback: null,
    services.GetRequiredService<ICostLedger>(),
    summarization));

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

// Every endpoint under /v1 requires a valid token unless it says otherwise. Opt-out rather than opt-in,
// because an endpoint someone forgot to protect is the failure that actually happens.
builder.Services.AddRequestTimeouts();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// Liveness only: checks no dependencies and returns no data (INV-7, INV-10).
app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")));

app.MapOpenApi("/swagger/v1/swagger.json");

var v1 = app.MapGroup("/v1").RequireAuthorization();
_ = v1.MapMe();
_ = v1.MapSummarize();

await app.RunAsync();

internal sealed record HealthResponse(string Status);

/// <summary>Visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
