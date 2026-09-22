using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tests.Auth;

/// <summary>
/// Enrolling a device by hand, so that the drafting path can be run end to end before there is an
/// enrolment flow to do it properly (ST-010, ST-063).
///
/// ST-010 will issue device tokens as part of signing in. It does not exist, and until it does a client
/// has nothing to put in an Authorization header — so the path built in #123 could be tested and could
/// not be <em>run</em>. This is the smallest thing that closes that: seed a tenant, a user and a device,
/// and print a token for them.
///
/// <b>Development only, and it refuses rather than warns.</b> A utility that mints credentials is the
/// last thing that should be reachable in a deployment, and "we remembered not to call it" is not a
/// control. Running the production container with the flag set is an accident somebody will have, and
/// the answer to it is an exception.
///
/// It is not an endpoint and must never become one. Calling it needs the process, the database
/// connection string and the signing key, which is to say it needs everything an attacker who could use
/// it would already have.
/// </summary>
public sealed class DevEnrolmentTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task OutsideDevelopmentItRefuses()
    {
        // The claim that matters. Everything else here is a convenience; this is the control.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DevEnrolment.EnrolAsync(db, api.Issuer, isDevelopment: false, TestContext.Current.CancellationToken));

        Assert.Contains("development", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NothingIsWrittenWhenItRefuses()
    {
        // A refusal that had already created the tenant would leave a credential behind in the database
        // it was refusing to create one in.
        var before = await api.UseAsync(db => db.Devices.CountAsync(TestContext.Current.CancellationToken));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DevEnrolment.EnrolAsync(db, api.Issuer, isDevelopment: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, await api.UseAsync(db => db.Devices.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ItIssuesATokenTheApiActuallyAccepts()
    {
        // The point of it. A token that parses but that /v1/me refuses is a token that wasted an
        // afternoon, so the check is the endpoint rather than the string.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
        var enrolled = await DevEnrolment.EnrolAsync(db, api.Issuer, isDevelopment: true, TestContext.Current.CancellationToken);

        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.Token);
        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"/v1/me answered {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task RunningItTwiceEnrolsOneDeviceRatherThanTwo()
    {
        // It will be run again: a token lasts an hour and the loop it exists for takes longer than that
        // to get working. A second device every time would leave a trail of credentials nobody revokes.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();

        var first = await DevEnrolment.EnrolAsync(db, api.Issuer, isDevelopment: true, TestContext.Current.CancellationToken);
        var again = await DevEnrolment.EnrolAsync(db, api.Issuer, isDevelopment: true, TestContext.Current.CancellationToken);

        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.Equal(1, await api.UseAsync(db => db.Devices.CountAsync(d => d.Name == DevEnrolment.DeviceName, TestContext.Current.CancellationToken)));
    }
}
