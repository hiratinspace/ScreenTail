using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// Guessing invite codes from many addresses at once (ST-010 follow-up, ADR-0009 "Guessing invite
/// codes"). The rate limit counts per address, so what stops a guesser with a botnet or an IPv6 /48 is
/// the code itself: twenty characters drawn uniformly from 31, about 99 bits. A refusal says the same
/// thing whatever invites exist and names no tenant, and the operator is warned when refusals pile up
/// across addresses, without anybody being refused for it.
/// </summary>
public sealed partial class InviteGuessingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly Uri Activate = new("/v1/devices/activate", UriKind.Relative);

    [GeneratedRegex("^[ABCDEFGHJKMNPQRSTUVWXYZ2-9]{5}(-[ABCDEFGHJKMNPQRSTUVWXYZ2-9]{5}){3}$")]
    private static partial Regex TodaysShape();

    [Fact]
    public void ANewCodeIsTwentyCharactersOfTheAlphabetInFourGroupsOfFiveAndAtLeastEightyBits()
    {
        Assert.Equal(31, Invites.Alphabet.Distinct().Count());
        Assert.DoesNotContain(Invites.Alphabet, c => "01ILO".Contains(c, StringComparison.Ordinal));
        Assert.True(Invites.EntropyBits >= 80, $"{Invites.EntropyBits} bits");
        Assert.InRange(Invites.EntropyBits, 99.0, 99.2);

        var codes = Enumerable.Range(0, 2_000).Select(_ => Invites.NewCode()).ToList();

        Assert.All(codes, code => Assert.Matches(TodaysShape(), code));
        Assert.All(codes, code => Assert.True(Invites.IsWellFormed(Invites.Normalise(code))));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCharacterOfTheAlphabetIsEquallyLikely()
    {
        // A byte taken modulo 31 makes the first eight characters one ninth likelier than the other
        // twenty-three (256 = 8 x 31 + 8), which is how codes were drawn before. 400,000 characters put
        // each count about 12,900, with a standard deviation near 112: five per cent either way is more
        // than five deviations, and the old bias (+9 per cent) is well outside it.
        var counts = new Dictionary<char, int>();
        for (var i = 0; i < 20_000; i++)
        {
            foreach (var c in Invites.Normalise(Invites.NewCode()))
            {
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        var expected = 20_000.0 * Invites.CodeLength / Invites.Alphabet.Length;
        Assert.Equal(Invites.Alphabet.Order().ToArray(), counts.Keys.Order().ToArray());
        Assert.All(counts, pair => Assert.InRange(pair.Value, expected * 0.95, expected * 1.05));
    }

    [Fact]
    public async Task ACodeIsAcceptedTypedInLowerCaseWithSpacesOrWithoutItsDashes()
    {
        var tenantId = await TenantAsync("Lowercase IT", seats: 5);
        var typed = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "low@acme.example", "Low", TimeProvider.System));
        var bare = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "bare@acme.example", "Bare", TimeProvider.System));
        using var client = api.CreateClient();

        using var lower = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(" " + typed.Code.ToLowerInvariant().Replace('-', ' ') + " ", "LOW"), TestContext.Current.CancellationToken);
        using var undashed = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(bare.Code.Replace("-", string.Empty, StringComparison.Ordinal), "BARE"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.OK, undashed.StatusCode);
    }

    [Fact]
    public async Task AnOldTenCharacterCodeIsStillAcceptedUntilItExpires()
    {
        // Codes issued before the change were ten characters. Seeded as the old code path stored them:
        // the hash of the normalised code. Assembled at run time, so no code-shaped literal is committed.
        var tenantId = await TenantAsync("Legacy IT", seats: 5);
        var live = LegacyCode();
        var stale = LegacyCode();
        _ = await api.UseAsync(async db =>
        {
            db.Invites.Add(new Invite { Id = Guid.NewGuid(), TenantId = tenantId, Email = "old@legacy.example", DisplayName = "Old", CodeHash = Invites.Hash(live), CreatedAt = DateTimeOffset.UtcNow.AddHours(-71), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            db.Invites.Add(new Invite { Id = Guid.NewGuid(), TenantId = tenantId, Email = "older@legacy.example", DisplayName = "Older", CodeHash = Invites.Hash(stale), CreatedAt = DateTimeOffset.UtcNow.AddHours(-73), ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) });
            return await db.SaveChangesAsync();
        });
        using var client = api.CreateClient();

        using var accepted = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(live, "OLD-CODE"), TestContext.Current.CancellationToken);
        using var expired = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(stale, "STALE-CODE"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Legacy IT", (await accepted.Content.ReadFromJsonAsync<ActivatedDevice>(TestContext.Current.CancellationToken))!.TenantName);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Contains("invite_expired", await expired.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyCodeNobodyIssuedIsAnsweredWithTheSameBytesWhateverItsShape()
    {
        // Live invites in two tenants, so a guess has something to miss. Every miss, well-formed in
        // either length or malformed in any way, is the same 400 with the same body: nothing says which
        // tenants exist, whether a code was close, or that its shape was checked first. An empty code
        // and one of only dashes used to reach the hash and answer 500.
        var acme = await TenantAsync("Acme Guessing IT", seats: 5);
        var birch = await TenantAsync("Birch Guessing Legal", seats: 5);
        _ = await api.UseAsync(db => Invites.CreateAsync(db, acme, "a@acme.example", "A", TimeProvider.System));
        _ = await api.UseAsync(db => Invites.CreateAsync(db, birch, "b@birch.example", "B", TimeProvider.System));
        var unknown = Invites.NewCode();
        string?[] misses =
        [
            unknown,
            unknown.ToLowerInvariant(),
            LegacyCode(),
            unknown[..^1],
            unknown + "A",
            unknown.Replace(unknown[0], 'O'),
            unknown.Replace(unknown[1], '0'),
            new string('Z', 400),
            "-----",
            string.Empty,
            null,
        ];
        using var client = api.CreateClient();

        var answers = new List<(HttpStatusCode Status, string? Type, string Body)>();
        foreach (var miss in misses)
        {
            using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(miss!, "GUESS"), TestContext.Current.CancellationToken);
            answers.Add((response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        }

        Assert.All(answers, a => Assert.Equal(answers[0], a));
        Assert.Equal(HttpStatusCode.BadRequest, answers[0].Status);
        Assert.Contains("invite_unknown", answers[0].Body, StringComparison.Ordinal);
        Assert.Contains(DevicesEndpoint.UnknownCodeMessage, answers[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRefusalNamesTheTenantTheInviteOrThePersonInvited()
    {
        // Each refusal a code can get: unknown, used, expired, the seat limit and a switched-off tenant.
        // The last three are reachable only with a code that was issued, and still name nobody: a tenant's
        // name comes back only with a device that activated (INV-10).
        const string name = "Quill Refusals Ltd";
        var tenantId = await TenantAsync(name, seats: 1);
        var first = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "first@quill.example", "First Person", TimeProvider.System));
        var full = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "full@quill.example", "Full Person", TimeProvider.System));
        var stale = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "stale@quill.example", "Stale Person", new FixedTime(DateTimeOffset.UtcNow - Invites.Lifetime - TimeSpan.FromMinutes(1))));
        using var client = api.CreateClient();
        using var activated = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(first.Code, "QUILL-1"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        var refusals = new List<(string Kind, HttpStatusCode Status, string Body)>();
        async Task TryAsync(string kind, string code)
        {
            using var response = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(code, "QUILL-2"), TestContext.Current.CancellationToken);
            refusals.Add((kind, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        }

        await TryAsync("unknown", Invites.NewCode());
        await TryAsync("used", first.Code);
        await TryAsync("expired", stale.Code);
        await TryAsync("seat_limit", full.Code);
        _ = await api.UseAsync(db => db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(set => set.SetProperty(t => t.DisabledAt, DateTimeOffset.UtcNow)));
        await TryAsync("tenant_disabled", full.Code);

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.Forbidden],
            refusals.Select(r => r.Status));
        Assert.Contains(DevicesEndpoint.SeatLimitMessage, refusals[3].Body, StringComparison.Ordinal);
        string[] secrets = [name, "Quill", tenantId.ToString(), tenantId.ToString("N"), "quill.example", "Person", first.Id.ToString(), full.Id.ToString(), stale.Id.ToString()];
        Assert.All(refusals, r => Assert.All(secrets, s => Assert.DoesNotContain(s, r.Body, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ARefusedCodeIsCountedAcrossAddressesAndNothingElseIs()
    {
        var watch = api.Services.GetRequiredService<ActivationWatch>();
        var tenantId = await TenantAsync("Counted IT", seats: 1);
        var good = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "good@counted.example", "Good", TimeProvider.System));
        var seatless = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "seatless@counted.example", "Seatless", TimeProvider.System));
        using var client = api.CreateClient();
        var before = watch.RefusedInWindow;

        using var unknown = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(Invites.NewCode(), "X"), TestContext.Current.CancellationToken);
        using var malformed = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest("nope", "X"), TestContext.Current.CancellationToken);
        Assert.Equal(before + 2, watch.RefusedInWindow);

        using var ok = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(good.Code, "GOOD"), TestContext.Current.CancellationToken);
        using var seat = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(seatless.Code, "SEATLESS"), TestContext.Current.CancellationToken);
        using var badName = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(Invites.NewCode(), " "), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, seat.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal(before + 2, watch.RefusedInWindow);

        using var used = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest(good.Code, "AGAIN"), TestContext.Current.CancellationToken);
        Assert.Equal(before + 3, watch.RefusedInWindow);
    }

    [Fact]
    public async Task AnActivationBodyPastSixteenKilobytesIsRefusedBeforeItIsRead()
    {
        using var client = api.CreateClient();
        using var content = new ByteArrayContent(new byte[(int)AdminSession.AnonymousBodyLimit + 1]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync(Activate, content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public void TheWatchWarnsOnceAWindowWithTwoNumbersAndNothingElse()
    {
        var clock = new HeldClock();
        var log = new Written();
        var watch = new ActivationWatch(new ActivationWatchOptions { WarnAt = 3, Window = TimeSpan.FromMinutes(10) }, clock, log);

        watch.Refused();
        watch.Refused();
        Assert.Empty(log.Entries);
        watch.Refused();
        watch.Refused();
        clock.Advance(TimeSpan.FromMinutes(9));
        watch.Refused();

        var (level, message, values) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith("3 device activations were refused for their invite code within 10 minutes", message, StringComparison.Ordinal);
        Assert.Equal(["Refused", "WindowMinutes", "{OriginalFormat}"], values.Select(v => v.Key));

        // A new window counts from nothing and may warn again.
        clock.Advance(TimeSpan.FromMinutes(1));
        watch.Refused();
        watch.Refused();
        Assert.Single(log.Entries);
        watch.Refused();
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(3, watch.RefusedInWindow);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1_000_001, 10)]
    [InlineData(50, 0)]
    [InlineData(50, 24 * 60 + 1)]
    public void AWatchThatWouldWarnAboutNothingOrNeverIsRefusedAtStartup(int warnAt, int minutes)
    {
        var options = new ActivationWatchOptions { WarnAt = warnAt, Window = TimeSpan.FromMinutes(minutes) };
        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void TheDefaultsWarnAtFiftyInTenMinutes()
    {
        var options = new ActivationWatchOptions();
        options.Validate();
        Assert.Equal((50, TimeSpan.FromMinutes(10)), (options.WarnAt, options.Window));
    }

    private static string LegacyCode()
    {
        var chars = RandomNumberGenerator.GetString(Invites.Alphabet, Invites.LegacyCodeLength);
        return $"{chars[..5]}-{chars[5..]}";
    }

    private Task<Guid> TenantAsync(string name, int seats) => api.UseAsync(async db =>
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = name, Seats = seats, CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        _ = await db.SaveChangesAsync();
        return tenant.Id;
    });

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>What the watch logged, with the values the sink would see beside the line.</summary>
    private sealed class Written : ILogger<ActivationWatch>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
        }
    }
}
