using System.Globalization;
using System.Net;

namespace ScreenTail.Api.Hosting;

/// <summary>
/// The container's own health check (ST-007, <c>deploy/compose.yaml</c>). The runtime image has no shell
/// and no curl, on purpose, so the check is this program run again with <c>--health-probe</c>: it asks
/// the API on this machine for <c>/health</c> and exits 0 for a 200 and 1 for anything else, without
/// building the host, reading configuration or touching the database.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-probe";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// <c>/health</c> on loopback, on the first port of <c>ASPNETCORE_HTTP_PORTS</c> (the .NET images set
    /// 8080), so the probe asks the port the server listens on and never another machine.
    /// </summary>
    public static Uri Target(string? httpPorts)
    {
        var first = (httpPorts ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var port = int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 8080;
        return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/health");
    }

    /// <summary>0 when <paramref name="target"/> answers 200 within the timeout, 1 otherwise. Loopback only.</summary>
    public static async Task<int> RunAsync(Uri target, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsLoopback || target.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("The health probe asks this machine over plain HTTP only.", nameof(target));
        }

        // No proxy from the environment: a probe sent to a proxy would report the proxy's health.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = timeout };
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
