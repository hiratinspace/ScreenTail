using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Platform.Lifecycle;

namespace ScreenTail.Service.Host;

/// <summary>
/// The capture service's check of itself at every start (ST-112, INV-6): the rule is <see cref="StartGuard"/>
/// in Core, the reads are <see cref="StartFactsReader"/>, shared with the UI.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceStart
{
    /// <summary>
    /// Says why and returns the exit code (0) when this process may not be the service; null to go on. The
    /// sentence goes to standard error and to the Application log (<see cref="RefusalEventLog"/>), because an
    /// installed build's standard error goes nowhere. Program.cs calls it before the single-instance name.
    /// </summary>
    /// <param name="record">Where a refusal is recorded. The Application log unless a test says otherwise.</param>
    public static int? RefuseIfUnfit(StartFacts facts, TextWriter error, Func<StartRefusal, bool>? record = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var refusal = StartGuard.Decide(facts);
        if (refusal == StartRefusal.None)
        {
            return null;
        }

        error.WriteLine(StartGuard.Explain(refusal));
        _ = (record ?? (r => RefusalEventLog.Write(r, ui: false)))(refusal);
        return 0;
    }

    /// <summary>This process's facts.</summary>
    public static StartFacts Read() => StartFactsReader.Read();
}
