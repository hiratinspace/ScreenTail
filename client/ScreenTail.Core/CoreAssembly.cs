using System.Reflection;

namespace ScreenTail.Core;

/// <summary>
/// Anchor for the platform-neutral core (ADR-0002): session state machine, store logic, redaction patterns,
/// timeline alignment and bundle building live here so their tests run on any OS.
/// </summary>
public static class CoreAssembly
{
    public static Assembly Reference { get; } = typeof(CoreAssembly).Assembly;
}
