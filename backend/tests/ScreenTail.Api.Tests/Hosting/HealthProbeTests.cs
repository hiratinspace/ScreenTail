using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using ScreenTail.Api.Hosting;

namespace ScreenTail.Api.Tests.Hosting;

/// <summary>
/// The container's health check (ST-007): exit 0 only for a 200 from /health on this machine, and 1 for
/// an error, a refusal, a server that is not there and one that does not answer in time.
/// </summary>
public sealed class HealthProbeTests
{
    [Fact]
    public async Task A200IsHealthyAndAnythingElseIsNot()
    {
        await using var server = await ServeAsync(HttpStatusCode.OK, TimeSpan.Zero);
        await using var failing = await ServeAsync(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero);
        await using var redirecting = await ServeAsync(HttpStatusCode.Found, TimeSpan.Zero);

        Assert.Equal(0, await HealthProbe.RunAsync(server.Health, HealthProbe.Timeout));
        Assert.Equal(1, await HealthProbe.RunAsync(failing.Health, HealthProbe.Timeout));
        Assert.Equal(1, await HealthProbe.RunAsync(redirecting.Health, HealthProbe.Timeout));
    }

    [Fact]
    public async Task NobodyListeningAndASlowAnswerAreUnhealthy()
    {
        Uri closed;
        await using (var gone = await ServeAsync(HttpStatusCode.OK, TimeSpan.Zero))
        {
            closed = gone.Health;
        }

        await using var slow = await ServeAsync(HttpStatusCode.OK, TimeSpan.FromSeconds(5));

        Assert.Equal(1, await HealthProbe.RunAsync(closed, HealthProbe.Timeout));
        Assert.Equal(1, await HealthProbe.RunAsync(slow.Health, TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health")]
    [InlineData("", "http://127.0.0.1:8080/health")]
    [InlineData("8080", "http://127.0.0.1:8080/health")]
    [InlineData("5000;5001", "http://127.0.0.1:5000/health")]
    [InlineData(" 9000 , 9001", "http://127.0.0.1:9000/health")]
    [InlineData("not-a-port", "http://127.0.0.1:8080/health")]
    [InlineData("70000", "http://127.0.0.1:8080/health")]
    public void TheTargetIsLoopbackOnTheServersFirstPort(string? ports, string expected) =>
        Assert.Equal(new Uri(expected), HealthProbe.Target(ports));

    [Theory]
    [InlineData("http://192.0.2.10:8080/health")]
    [InlineData("http://example.com/health")]
    [InlineData("https://127.0.0.1:8443/health")]
    public async Task ItAsksNoOtherMachineAndOnlyPlainHttp(string target) =>
        _ = await Assert.ThrowsAsync<ArgumentException>(() => HealthProbe.RunAsync(new Uri(target), HealthProbe.Timeout));

    private static async Task<Served> ServeAsync(HttpStatusCode status, TimeSpan delay)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        _ = app.MapGet("/health", async (HttpContext context) =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, context.RequestAborted);
            }

            context.Response.StatusCode = (int)status;
            if (status == HttpStatusCode.Found)
            {
                context.Response.Headers.Location = "/elsewhere";
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Urls.Single();
        return new Served(app, new Uri(new Uri(address), "/health"));
    }

    private sealed record Served(WebApplication App, Uri Health) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
