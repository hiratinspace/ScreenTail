namespace ScreenTail.Core.Net;

/// <summary>Why a request is allowed out, which is also what the audit row says (ST-045).</summary>
public enum EgressPurpose
{
    /// <summary>Drafting a note in the cloud (ST-063). The first thing local-only mode stops.</summary>
    Summarisation,

    /// <summary>Anything the client sends the backend on its own: telemetry, policy sync, licence checks.</summary>
    Backend,

    /// <summary>
    /// The technician pressed Publish. Allowed even in local-only mode, because they asked for it and
    /// know where it is going — INV-8 draws the line at "user-initiated", not at "no network".
    /// </summary>
    Publish,

    /// <summary>The tenant's policy, fetched at start and hourly (ST-047). Allowed in local-only mode: it carries no content, and it is how local-only is lifted.</summary>
    Policy,

    /// <summary>
    /// Fetching a speech model (ST-027). Not customer data, but still egress, so still decided here, and
    /// refused in local-only mode like everything the client does on its own: local-only is a promise
    /// about requests, not about what is in them (2026-10-12 review, S-32). Narration under local-only
    /// needs the model on the machine already; the recogniser treats the refusal as "no narration".
    /// </summary>
    ModelDownload,

    /// <summary>
    /// Asking the update feed whether there is a newer release, and downloading it (ST-113). Carries no
    /// content: the channel and the architecture, in the path. Only to the update hosts compiled into the
    /// client, and refused under local-only unless the tenant's admin allowed it, because a request the
    /// technician did not ask for is still a request (INV-8).
    /// </summary>
    UpdateCheck,
}

/// <param name="Allowed">False means the request is never made.</param>
/// <param name="Reason">Plain language for the log and the diagnostics panel. Never a URL with a path.</param>
public sealed record EgressDecision(bool Allowed, string Reason);

/// <param name="LocalOnly">Nothing leaves except a publish the technician asked for and the policy fetch that can lift it (INV-8).</param>
/// <param name="PolicyEnforced">
/// The tenant's admin set this, so the technician cannot turn it off here (INV-11). A setting that says
/// "local only" while the UI lets it be switched off is not a guarantee, it is a label.
/// </param>
/// <param name="PublishHosts">
/// The PSA and documentation hosts a publish may reach. Exact host names, never suffixes: a rule matching
/// "*.example.com" is a rule that trusts anyone who can register a subdomain.
/// </param>
public sealed record EgressSettings
{
    public bool LocalOnly { get; init; }

    public bool PolicyEnforced { get; init; }

    public IReadOnlySet<string> PublishHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> BackendHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ModelHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The update feed's host and the package hosts, from the trust file compiled into the client (ST-113).</summary>
    public IReadOnlySet<string> UpdateHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tenant's admin allows update checks while local-only is on (ST-113). Only a policy sets it; the
    /// technician's own local-only stops update checks like everything else the client does on its own.
    /// </summary>
    public bool UpdatesUnderLocalOnly { get; init; }

    /// <summary>
    /// The deployment's shape, as the service builds it: the speech model hosts, and the one backend host
    /// on both the backend list and the publish list. Publishing goes to the backend too, because the
    /// PSA's credentials live there and never here, so under local-only the purpose a sender attaches is
    /// what separates a publish from a draft (INV-8). The host is the address that was configured, not
    /// whatever a server later claims to be. No backend means both lists are empty.
    /// </summary>
    public static EgressSettings ForBackend(Uri? backend, IReadOnlySet<string> modelHosts)
    {
        ArgumentNullException.ThrowIfNull(modelHosts);
        return new EgressSettings
        {
            ModelHosts = modelHosts,
            BackendHosts = Hosts(backend),
            PublishHosts = Hosts(backend),
        };
    }

    private static HashSet<string> Hosts(Uri? backend) => backend is null
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { backend.Host };
}

/// <summary>
/// Decides whether a request may leave the machine (ST-046, INV-8).
///
/// INV-8 says local-only mode is "enforced by an HTTP allowlist, not by convention", and the difference
/// is the whole ticket. A convention is a rule every future caller has to remember; an allowlist is a rule
/// they cannot get past. So this is a pure decision function and <see cref="EgressGuard"/> is the handler
/// that no <c>HttpClient</c> in the service can be built without.
///
/// <b>Two independent reasons a request is refused</b>, and they are not the same reason. Local-only mode
/// blocks a purpose: no summarisation, no backend chatter, whatever the host. The allowlist blocks a host:
/// even with local-only off, a publish may only reach the PSA hosts the tenant configured. A request has
/// to pass both, so a bug that clears one flag does not open everything.
///
/// <b>Anything it has not been told about is refused.</b> A new caller with a purpose this does not know,
/// or a host nobody listed, does not get a default of "probably fine" — the failure mode of a default-open
/// egress guard is a customer's screenshots reaching somewhere nobody chose.
/// </summary>
public sealed class EgressPolicy(EgressSettings? settings = null)
{
    // Volatile: the policy applier replaces it on the thread a fetch or a save arrived on, and every
    // request after that, on whatever thread sends it, has to be decided under the new settings.
    private volatile EgressSettings _settings = settings ?? new EgressSettings();

    public EgressSettings Settings => _settings;

    /// <summary>
    /// The tenant's policy arriving (ST-047): local-only on or off, whether the admin locked it, and
    /// whether the admin lets update checks through it (ST-113). The hosts are the deployment's and do
    /// not change here.
    /// </summary>
    public void Apply(bool localOnly, bool enforced, bool updatesUnderLocalOnly = false) =>
        _settings = _settings with { LocalOnly = localOnly, PolicyEnforced = enforced, UpdatesUnderLocalOnly = updatesUnderLocalOnly };

    /// <summary>
    /// True when the technician may change local-only mode. False when the tenant's admin set it (INV-11):
    /// Settings shows the toggle locked with the policy's name rather than hiding it, so the reason a
    /// thing cannot be changed is visible.
    /// </summary>
    public bool CanChangeLocally => !_settings.PolicyEnforced;

    /// <summary>What the tray tooltip and the HUD show (ST-046, Spec §5 S1/S2).</summary>
    public string? Badge => _settings.LocalOnly
        ? _settings.PolicyEnforced ? "Local-only (set by your administrator)" : "Local-only"
        : null;

    public EgressDecision Decide(EgressPurpose purpose, Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // A request that is not over TLS is refused whatever else is true. Everything this client sends is
        // either customer data or something that authenticates to it.
        if (!string.Equals(destination.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new EgressDecision(false, $"{destination.Scheme} is not encrypted.");
        }

        if (_settings.LocalOnly && purpose is EgressPurpose.Summarisation or EgressPurpose.Backend)
        {
            // The criterion: zero requests at finalize. Drafting happens on the device instead (ST-065).
            return new EgressDecision(false, "Local-only mode is on, so nothing is sent for drafting.");
        }

        if (_settings.LocalOnly && purpose is EgressPurpose.ModelDownload)
        {
            // The one request that is not about a customer, and still a request the technician did not
            // ask for. The policy fetch is the only thing local-only lets out on its own, because it is
            // how local-only is lifted; a model is not (S-32).
            return new EgressDecision(false, "Local-only mode is on, so the speech model is not fetched.");
        }

        if (_settings.LocalOnly && purpose is EgressPurpose.UpdateCheck && !(_settings.PolicyEnforced && _settings.UpdatesUnderLocalOnly))
        {
            // ST-113. Fail closed: an update check is a request nobody asked for at that moment, so it
            // waits like the model download does, unless the tenant's admin locked local-only on and
            // chose to let update checks through it. A technician's own local-only lets nothing out.
            return new EgressDecision(false, "Local-only mode is on, so ScreenTail does not check for updates.");
        }

        var allowed = purpose switch
        {
            EgressPurpose.Publish => _settings.PublishHosts,
            EgressPurpose.Summarisation or EgressPurpose.Backend or EgressPurpose.Policy => _settings.BackendHosts,
            EgressPurpose.ModelDownload => _settings.ModelHosts,
            EgressPurpose.UpdateCheck => _settings.UpdateHosts,
            _ => null,
        };

        if (allowed is null)
        {
            return new EgressDecision(false, "That kind of request has no allowed destinations.");
        }

        return allowed.Contains(destination.Host)
            ? new EgressDecision(true, "Allowed.")

            // The host is named and nothing else is, so the message can say which without saying what was
            // being sent or to what path (INV-10).
            : new EgressDecision(false, $"{destination.Host} is not on the allowed list for this tenant.");
    }
}
