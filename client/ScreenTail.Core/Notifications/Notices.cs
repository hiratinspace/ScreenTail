using ScreenTail.Core.Outbox;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Notifications;

/// <summary>What pressing the notification does. Spec §6 gives every message at most one.</summary>
public enum NotificationAction
{
    /// <summary>Nothing to press. Used where there is genuinely nothing a technician can do yet.</summary>
    None,

    /// <summary>Open Review for the session the notification is about.</summary>
    Review,

    /// <summary>Open the session, whatever state it is in. The failure paths use this.</summary>
    Open,
}

/// <param name="Text">Spec §6's wording, verbatim. Shown to a technician mid-session.</param>
/// <param name="Key">
/// What makes this notification the same notification. The service repeats state on every reconnect and
/// on every <c>get_state</c>, and two identical toasts say the thing happened twice.
/// </param>
public sealed record Notification(string Text, NotificationAction Action, string Key);

/// <summary>
/// What ScreenTail says, and when it says nothing (ST-073, Spec §6).
///
/// <b>Silence is the default and the most important case.</b> Spec §4 forbids interrupting a recording,
/// and a toast over a customer's screen during a support call is the one notification that can do real
/// damage — it is visible to the person on the other end, and it arrives at the moment the technician's
/// attention is worth most. Nothing is said while a session is running, including about work left over
/// from the last one.
///
/// The wording is Spec §6's and is asserted verbatim by the tests. That is not pedantry: these are the
/// only words a technician reads while looking at somebody else's machine, and each one names its object
/// and offers the next step because "something went wrong" costs a support call.
/// </summary>
public static class Notices
{
    /// <returns>What to say, or null to say nothing.</returns>
    public static Notification? For(CaptureStateSnapshot? capture, OutboxWaiting waiting = default)
    {
        if (capture is null)
        {
            // The UI has not heard from the service. The HUD says so already; a toast would be noise
            // about a state that usually resolves itself in a second.
            return null;
        }

        // Spec §4: never interrupt recording. Everything below waits until the session is over.
        if (capture.State is CaptureStates.Recording or CaptureStates.Paused
            or CaptureStates.Suppressed or CaptureStates.Finalizing)
        {
            return null;
        }

        return capture.State switch
        {
            CaptureStates.DraftReady => new Notification(
                $"Draft ready — {Describe(capture)}",
                NotificationAction.Review,
                $"draft-ready:{capture.SessionId}"),

            CaptureStates.DraftFailed => new Notification(
                DraftFailure.Headline,
                NotificationAction.Open,
                $"draft-failed:{capture.SessionId}"),

            _ => Queued(waiting),
        };
    }

    /// <summary>
    /// What the queue has to say for itself, when there is no session to talk about.
    ///
    /// Work nobody knows the outcome of comes first. The outbox parks such an attempt rather than
    /// repeating it — publishing twice is worse than publishing late — so it is the one state that will
    /// not resolve itself and the one that has to ask for a person.
    /// </summary>
    private static Notification? Queued(OutboxWaiting waiting)
    {
        if (waiting.Uncertain > 0)
        {
            return new Notification(
                "A publish may not have gone through. Check the ticket before sending it again.",
                NotificationAction.Open,
                $"outbox-uncertain:{waiting.Uncertain}");
        }

        if (waiting.Drafts > 0)
        {
            return new Notification(
                "Offline — your draft will be created when you're back online.",
                NotificationAction.None,
                "outbox-offline");
        }

        return null;
    }

    /// <summary>The session in the technician's words: its draft title and the customer it belongs to.</summary>
    private static string Describe(CaptureStateSnapshot capture) =>
        (capture.DraftTitle, capture.DraftCompany) switch
        {
            (not null, not null) => $"{capture.DraftTitle} · {capture.DraftCompany}",
            (not null, null) => capture.DraftTitle!,
            (null, not null) => capture.DraftCompany!,
            _ => "session ready to review",
        };
}

/// <summary>
/// Raises each notification once (ST-073).
///
/// The service repeats its state on every reconnect, on every <c>get_state</c> and after every UI
/// restart. Without this, closing and reopening the window would announce a draft that has been ready
/// for an hour, and two identical toasts would say the thing happened twice.
/// </summary>
public sealed class Notifier
{
    private string? _last;

    /// <returns>What to say about this state, or null when it has already been said.</returns>
    public Notification? Observe(CaptureStateSnapshot? capture, OutboxWaiting waiting = default)
    {
        var notice = Notices.For(capture, waiting);
        if (notice is null)
        {
            // Not cleared: a session that goes quiet does not make an old notification new again.
            return null;
        }

        if (notice.Key == _last)
        {
            return null;
        }

        _last = notice.Key;
        return notice;
    }
}
