using ScreenTrail.Shared.Ipc;

namespace ScreenTrail.Core.Sessions;

/// <summary>How a start by hand (Ctrl+Alt+R, or the tray's Start capture) came out.</summary>
public enum HandStartOutcome
{
    Started,

    /// <summary>Windows Installer is replacing ScreenTrail (ST-113): a hold on new sessions is in force.</summary>
    Held,

    /// <summary>A session is already under way or finishing up.</summary>
    Busy,

    /// <summary>The tray's start only: no remote tool window has been in front recently enough to start in.</summary>
    NoRemoteSession,

    /// <summary>Nothing on screen says capture is on, so nothing may start (INV-4).</summary>
    NotIndicated,
}

/// <summary>
/// The rules a start by hand meets, in the order it meets them, and what the tray says when one refuses
/// (ST-071, Spec §5 S1 as built v0.7.2). The service's Ctrl+Alt+R and the tray's Start capture both go
/// through the same start in the service's coordinator; this is the part of it that needs no window, in
/// Core so the Mac suite holds it.
///
/// The tray shows the sentence in a balloon, so each one says what happened and what to do, and stays
/// under the 255 characters a balloon holds.
/// </summary>
public static class HandStart
{
    /// <summary>No remote tool window was in front recently: the friendliest refusal, with the next step.</summary>
    public const string NoRemoteSession =
        "Nothing was started: no remote session has been in front in the last few minutes. Open the remote "
        + "session, then choose Start capture or press Ctrl+Alt+R.";

    /// <summary>The pill is not on screen. The tray's shorter form of <see cref="ManualStart.NotIndicated"/>.</summary>
    public const string NotIndicated =
        "Nothing was started: the recording pill is not on screen, and ScreenTrail never records without "
        + "showing it. Wait a few seconds, then try again.";

    /// <summary>A session is already under way (a race: the menu offers Start capture only between sessions).</summary>
    public const string Busy = "Nothing was started: a session is already being captured or finished.";

    /// <summary>
    /// What stops a start before any window is looked at, or null to go on: a hold first (nothing starts
    /// during an update, whatever else is true), then a session under way.
    /// </summary>
    public static HandStartOutcome? Before(bool held, SessionState state) =>
        held ? HandStartOutcome.Held
        : state is SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed or SessionState.DraftPending ? null
        : HandStartOutcome.Busy;

    /// <summary>The words for an outcome, or null when it started.</summary>
    public static string? Words(HandStartOutcome outcome) => outcome switch
    {
        HandStartOutcome.Started => null,
        HandStartOutcome.Held => ManualStart.Updating,
        HandStartOutcome.Busy => Busy,
        HandStartOutcome.NoRemoteSession => NoRemoteSession,
        HandStartOutcome.NotIndicated => NotIndicated,
        _ => Busy,
    };

    /// <summary>The pipe's answer to the tray's <c>start</c>.</summary>
    public static CommandResult Result(int requestId, HandStartOutcome outcome) => new()
    {
        RequestId = requestId,
        Ok = outcome == HandStartOutcome.Started,
        Error = Words(outcome),
    };
}
