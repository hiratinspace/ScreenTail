namespace ScreenTail.Api.Providers;

/// <summary>
/// Where the PSA and documentation clients may connect (security check 2026-10-14). A tenant chooses its
/// provider's site, so without a rule the backend would call whatever that site names: this machine, the
/// private network it sits on, or a cloud metadata address, and hand a line of each answer back.
///
/// The rule is <see cref="PublicNetwork"/>: public addresses only, checked when the connection is made.
/// <see cref="AllowPrivateAddresses"/> lifts it for a developer running a provider stand-in on their own
/// machine, and for tests. <b>It is refused outside Development</b>: the service does not start with it,
/// because a deployment that has it on has had its protection removed by a setting.
/// </summary>
public sealed class ProviderNetworkOptions
{
    public const string Section = "ProviderNetwork";

    public bool AllowPrivateAddresses { get; set; }

    /// <summary>Throws when the option is on anywhere but Development.</summary>
    public void Validate(bool isDevelopment)
    {
        if (AllowPrivateAddresses && !isDevelopment)
        {
            throw new InvalidOperationException(
                "ProviderNetwork:AllowPrivateAddresses is on outside Development. It lets a tenant's provider "
                + "site reach this server's own network, so the service refuses to start with it. Remove the setting.");
        }
    }
}
