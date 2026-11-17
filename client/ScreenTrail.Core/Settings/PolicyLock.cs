namespace ScreenTrail.Core.Settings;

/// <summary>
/// What Settings says under a setting the tenant's policy locked (INV-11, Spec v0.7.1). One wording for
/// every locked setting, in the owner's words for who manages something ("your ScreenTrail owner", as the
/// integration cards say it). The policy's id means nothing to a technician, so it is never in the visible
/// line; it stays in the line's tool tip, where support can ask for it.
/// </summary>
public static class PolicyLock
{
    public const string Line = "Set by your ScreenTrail owner";

    /// <summary>The tool tip on <see cref="Line"/>: where it is set, and the policy's id for support.</summary>
    public static string Detail(string policyVersion) =>
        $"Set in the web admin. Policy {policyVersion}, if support asks.";
}
