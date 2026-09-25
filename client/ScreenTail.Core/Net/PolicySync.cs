using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScreenTail.Core.Net;

/// <summary>
/// The tenant's policy as the backend states it (ST-047): what the admin decided about this device.
/// <paramref name="LocalOnlyLocked"/> is INV-11's word — locked, the technician cannot change it here.
/// </summary>
public sealed record TenantPolicy(string Version, int RetentionDays, bool LocalOnly, bool LocalOnlyLocked, bool CaptureAllWindows)
{
    /// <summary>What applies until a policy has ever been synced: the product's own defaults, named as such.</summary>
    public static TenantPolicy Default { get; } = new("default", 7, false, false, false);
}

/// <summary>Where the last synced policy lives between runs, so a backend that does not answer changes nothing (AC2).</summary>
public interface IPolicyCache
{
    TenantPolicy? Load();

    void Save(TenantPolicy policy);
}

/// <summary>A JSON file beside the store. Not a secret: a policy says what the client does, not what it saw.</summary>
public sealed class FilePolicyCache(string path) : IPolicyCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TenantPolicy? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TenantPolicy>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(TenantPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(policy, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The policy still applies for this run; the next fetch writes it again.
        }
    }
}

/// <param name="Enforced">The admin locked local-only, so Settings shows it read-only with "Set by your admin".</param>
public sealed record AppliedPolicy(string Version, TimeSpan Retention, bool LocalOnly, bool Enforced, bool CaptureAllWindows);

/// <summary>
/// What a policy means for this client, next to what the technician chose. Retention is always the
/// admin's; local-only is the admin's when locked and the technician's otherwise (INV-11).
/// </summary>
public static class PolicyApplication
{
    public static AppliedPolicy Resolve(TenantPolicy policy, bool userLocalOnly)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new AppliedPolicy(
            policy.Version,
            TimeSpan.FromDays(Math.Clamp(policy.RetentionDays, 1, 365)),
            policy.LocalOnlyLocked ? policy.LocalOnly : userLocalOnly,
            policy.LocalOnlyLocked,
            policy.CaptureAllWindows);
    }
}

/// <summary>
/// Fetches the tenant's policy at start and hourly and keeps the last one that arrived (ST-047). The
/// fetch goes under its own egress purpose, allowed in local-only mode, because the policy that turns
/// local-only on has to be able to turn it off again. Nothing here is content (INV-10).
/// </summary>
public sealed class PolicySync(HttpClient http, Func<string?> token, IPolicyCache cache, TimeProvider? time = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _token = token ?? throw new ArgumentNullException(nameof(token));
    private readonly IPolicyCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private TenantPolicy? _current;

    /// <summary>Raised when a fetch brought a policy that differs from the one in force.</summary>
    public event Action<TenantPolicy>? Changed;

    public TenantPolicy Current => _current ??= _cache.Load() ?? TenantPolicy.Default;

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>True when the backend answered with a policy; false leaves <see cref="Current"/> as it was.</summary>
    public async Task<bool> FetchAsync(CancellationToken ct = default)
    {
        if (_http.BaseAddress is null || _token() is not { Length: > 0 } bearer)
        {
            return false;
        }

        using var request = EgressRequest.For(HttpMethod.Get, new Uri("v1/policy", UriKind.Relative), EgressPurpose.Policy);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested) || ex is EgressBlockedException)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            TenantPolicy? policy;
            try
            {
                policy = await response.Content.ReadFromJsonAsync<TenantPolicy>(Json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return false;
            }

            if (policy is null || string.IsNullOrWhiteSpace(policy.Version))
            {
                return false;
            }

            LastSyncedAt = _time.GetUtcNow();
            var before = Current;
            _current = policy;
            _cache.Save(policy);
            if (policy != before)
            {
                Changed?.Invoke(policy);
            }

            return true;
        }
    }

    /// <summary>The hourly loop the host runs; sooner after a miss.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var fetched = await FetchAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(fetched ? Every : RetryAfterFailure, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
