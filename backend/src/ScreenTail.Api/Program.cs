var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Liveness only: checks no dependencies and returns no data (INV-7, INV-10).
app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")));

await app.RunAsync();

internal sealed record HealthResponse(string Status);

/// <summary>Visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
