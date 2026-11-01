using Microsoft.Extensions.Configuration;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>The web admin's settings and their closed defaults (ST-099, ADR-0009).</summary>
public sealed class AdminOptionsTests
{
    [Fact]
    public void TheDefaultsAreClosed()
    {
        var options = new AdminOptions();

        Assert.False(options.SignInConfigured);
        Assert.Empty(options.SignupAllowlist);
        Assert.False(options.MaySignUp("anyone@acme.example"));
        Assert.Equal(5, options.NewTenantSeats);
        Assert.Null(options.DownloadUri());
    }

    [Fact]
    public void TheSignupListBindsFromConfigurationAndIgnoresCase()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:GoogleClientId"] = GoogleTokens.ClientId,
                ["Admin:SignupAllowlist:0"] = "Owner@Acme.example",
                ["Admin:SignupAllowlist:1"] = " second@birch.example ",
            })
            .Build()
            .GetSection(AdminOptions.Section)
            .Get<AdminOptions>()!;

        Assert.True(options.SignInConfigured);
        Assert.True(options.MaySignUp("owner@acme.example"));
        Assert.True(options.MaySignUp("second@birch.example"));
        Assert.False(options.MaySignUp("third@acme.example"));
        Assert.False(options.MaySignUp(string.Empty));
    }

    [Theory]
    [InlineData("http://downloads.example/ScreenTail.msi")]
    [InlineData("downloads.example/ScreenTail.msi")]
    [InlineData("ftp://downloads.example/ScreenTail.msi")]
    public void ADownloadLinkThatIsNotHttpsIsRefusedAtStartup(string url) =>
        Assert.Throws<InvalidOperationException>(() => new AdminOptions { ClientDownloadUrl = url }.Validate(new JwtOptions()));

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void ASeatCountOutsideTheRangeIsRefused(int seats) =>
        Assert.Throws<InvalidOperationException>(() => new AdminOptions { NewTenantSeats = seats }.Validate(new JwtOptions()));

    [Theory]
    [InlineData("screentail-admin")]
    [InlineData("screentail-admin/")]
    [InlineData("Screentail-Admin")]
    [InlineData(" screentail-admin// ")]
    public void ADeviceAudienceEqualToTheAdminOneIsRefusedHoweverItIsSpelled(string audience)
    {
        // Configured the same, a device token would pass the admin scheme's audience check, and the token
        // handler ignores a trailing slash when it compares.
        var thrown = Assert.Throws<InvalidOperationException>(() => new AdminOptions().Validate(new JwtOptions { Audience = audience }));

        Assert.Contains("Jwt:Audience", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kelvin@acme.example")]
    [InlineData("kelvın@acme.example")]
    [InlineData("kelvin@acme.example​")]
    public void AnAddressThatIsNotAsciiCannotSignUpEvenIfItFoldsToOneOnTheList(string email)
    {
        var options = new AdminOptions();
        options.SignupAllowlist.Add("kelvin@acme.example");

        Assert.True(options.MaySignUp("KELVIN@acme.example"));
        Assert.False(options.MaySignUp(email));
    }
}
