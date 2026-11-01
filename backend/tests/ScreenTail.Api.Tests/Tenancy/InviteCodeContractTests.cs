using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// The invite code's shape and how a typed code is read, against <c>shared/contracts/invite-code.v1.json</c>,
/// the table the client's <c>InviteCodesTests</c> runs too (ST-010; ADR-0009, "Guessing invite codes").
/// A change to the normaliser on this side only (Unicode folding, a new separator) fails here or there.
/// And the hash a code is stored under is the hash it is looked up by: both are <see cref="Invites.Hash"/>.
/// </summary>
public sealed class InviteCodeContractTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    public static TheoryData<int> Cases() => [.. Enumerable.Range(0, Contract().GetProperty("cases").GetArrayLength())];

    [Fact]
    public void TheShapeIsTheContracts()
    {
        var contract = Contract();
        Assert.Equal(Invites.Alphabet, contract.GetProperty("alphabet").GetString());
        Assert.Equal(Invites.CodeLength, contract.GetProperty("length").GetInt32());
        Assert.Equal(Invites.LegacyCodeLength, contract.GetProperty("legacyLength").GetInt32());
        Assert.Equal(Invites.GroupLength, contract.GetProperty("groupLength").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachTypedCodeIsReadAsTheContractSays(int index)
    {
        var item = Contract().GetProperty("cases")[index];
        var typed = item.GetProperty("typed").GetString();

        var normalised = Invites.Normalise(typed);

        Assert.Equal(item.GetProperty("normalised").GetString(), normalised);
        Assert.Equal(item.GetProperty("wellFormed").GetBoolean(), Invites.IsWellFormed(normalised));
        Assert.Equal(item.GetProperty("formatted").GetString(), Invites.Format(normalised));
    }

    [Fact]
    public async Task ACodeIsStoredAndLookedUpByTheSameHash()
    {
        var tenantId = Guid.NewGuid();
        var issued = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Hashing IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            _ = await db.SaveChangesAsync();
            return await Invites.CreateAsync(db, tenantId, "hash@hashing.example", "Hash", TimeProvider.System);
        });

        // Creation stores Invites.Hash of the code it hands out.
        Assert.Equal(Invites.Hash(issued.Code), await api.UseAsync(db => db.Invites.Where(i => i.Id == issued.Id).Select(i => i.CodeHash).SingleAsync()));

        // And activation finds a row stored under Invites.Hash, whatever form the code is typed in.
        var typed = Invites.NewCode();
        _ = await api.UseAsync(async db =>
        {
            db.Invites.Add(new Invite { Id = Guid.NewGuid(), TenantId = tenantId, Email = "typed@hashing.example", DisplayName = "Typed", CodeHash = Invites.Hash(typed.ToLowerInvariant()), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            return await db.SaveChangesAsync();
        });
        using var client = api.CreateClient();
        using var activated = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(typed.Replace("-", " ", StringComparison.Ordinal), "TYPED"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        // The endpoint calls Invites.Hash itself rather than a hash of its own, so a change to how codes
        // are hashed (a pepper, a new scheme) changes creation and lookup together.
        var endpoint = File.ReadAllText(SourceFile("Endpoints", "DevicesEndpoint.cs"));
        Assert.Contains("Invites.Hash(code)", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceTokens.Hash(code", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceTokens.Hash(Invites.Normalise", endpoint, StringComparison.Ordinal);
    }

    private static JsonElement Contract()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.invite-code.v1.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string SourceFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, "src", "ScreenTail.Api", .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
