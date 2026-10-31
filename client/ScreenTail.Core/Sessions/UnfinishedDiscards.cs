namespace ScreenTail.Core.Sessions;

/// <summary>
/// What a pass over the discarded sessions could not finish deleting (review of #243). The sessions are
/// discarded and their content unreachable whatever this says; it is for a log and the next try.
/// </summary>
/// <param name="FirstFault">The first fault's type name, never its message (INV-10).</param>
/// <param name="Sessions">How many discarded sessions still hold content after the pass.</param>
public sealed record UnfinishedDiscards(string FirstFault, int Sessions);
