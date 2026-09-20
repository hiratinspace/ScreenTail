namespace ScreenTail.Core.Ipc;

/// <summary>
/// Whether this process is running its own code and nothing else (ST-012).
///
/// The peer check verifies the file a process was started from. That is worth having and it is not
/// enough on its own, because .NET will load somebody else's code into a perfectly genuine, perfectly
/// signed process if the environment asks it to. <c>DOTNET_STARTUP_HOOKS</c> names assemblies whose
/// initialiser runs before <c>Main</c>; it exists for profilers and it is a documented way to run
/// arbitrary code inside a signed executable.
///
/// So an attacker needs no forged binary at all. Launch our real, signed UI with a hook set, and it
/// passes the signature check for the same reason the genuine one does — while somebody else's code is
/// already running inside it, holding the session token (2026-09-19 review).
///
/// <b>The defence is that the code being hijacked refuses to run.</b> The hijacked process is our code,
/// so our code gets to say no. A development build says so out loud rather than refusing, because these
/// variables are how a profiler is attached and a build nobody ships should stay debuggable.
/// </summary>
public static class RunningHonestly
{
    /// <summary>
    /// Environment variables that load code into this process before it starts.
    ///
    /// Startup hooks run an initialiser before Main. The profiler pair loads a native DLL with full
    /// access to the runtime; <c>CORECLR_ENABLE_PROFILING</c> is the switch and
    /// <c>CORECLR_PROFILER_PATH</c> is what it loads.
    /// </summary>
    private static readonly string[] LoadOthersCode =
    [
        "DOTNET_STARTUP_HOOKS",
        "CORECLR_ENABLE_PROFILING",
        "CORECLR_PROFILER_PATH",
    ];

    /// <summary>
    /// Which of them are set, in the order they are checked. Empty is the ordinary answer.
    ///
    /// Names only. The value is a path somebody chose and has no business in a log line that a customer
    /// may read (INV-10).
    /// </summary>
    public static IReadOnlyList<string> ForeignCodeRequested(Func<string, string?>? read = null)
    {
        var lookup = read ?? Environment.GetEnvironmentVariable;
        return [.. LoadOthersCode.Where(name => !string.IsNullOrWhiteSpace(lookup(name)))];
    }

    /// <summary>
    /// Why this process must not start, or null when it may.
    ///
    /// <paramref name="development"/> turns the refusal into a caller-visible warning: a build nobody
    /// ships should still be attachable to a profiler, and a rule that makes debugging impossible is a
    /// rule somebody deletes.
    /// </summary>
    public static string? WhyNotToStart(bool development, Func<string, string?>? read = null)
    {
        var requested = ForeignCodeRequested(read);
        if (requested.Count == 0 || development)
        {
            return null;
        }

        return $"{string.Join(" and ", requested)} asks this process to load code that is not ScreenTail's, "
            + "so it will not start. Unset it and start ScreenTail again.";
    }
}
