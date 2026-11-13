using System.Globalization;

namespace ScreenTail.Core.Shell;

/// <summary>How the dialog's acting button is drawn: the danger style for what destroys something.</summary>
public enum ConfirmTone
{
    /// <summary>Discard, delete, remove: the danger button (Spec §3).</summary>
    Danger,

    /// <summary>Anything else: the one filled Iris button.</summary>
    Primary,
}

/// <summary>
/// One question in the app's one dialog (Spec §3 Modal, v0.6.11): what it asks, what follows, and the two
/// buttons in words that say what each does.
/// </summary>
/// <param name="Heading">The question, as the dialog's title and its first line.</param>
/// <param name="Consequence">What is lost or claimed, in plain words.</param>
/// <param name="Action">The acting button ("Discard 2 sessions"), or null for a notice with one button.</param>
/// <param name="Cancel">The button that changes nothing ("Keep it"). Esc presses it.</param>
/// <param name="Tone">Danger when the action destroys something.</param>
/// <param name="CancelIsDefault">Whether Enter presses Cancel when nothing has to be typed.</param>
public sealed record ConfirmWords(
    string Heading,
    string Consequence,
    string? Action,
    string Cancel,
    ConfirmTone Tone,
    bool CancelIsDefault = true);

/// <summary>
/// The words of every question the client asks in a dialog, in one place so a test holds them all to the
/// copy rules (Spec §4, §6): plain words, the button says what happens, never "OK", "Yes" or "Confirm".
/// Shown, never logged (INV-10): a session is named only by its start, length and tool.
/// </summary>
public static class ConfirmCopy
{
    /// <summary>Spec §5 S3 Discard and the tray's Discard session (S1), typed with DISCARD.</summary>
    public static ConfirmWords DiscardSession { get; } = new(
        "Discard this session?",
        "The screenshots, transcript and timeline are deleted from this device straight away, and the note goes with them. This cannot be undone.",
        "Discard session",
        "Keep it",
        ConfirmTone.Danger);

    /// <summary>Spec §5 S6 Delete everything, typed with the phrase the service gives.</summary>
    public static ConfirmWords DeleteEverything { get; } = new(
        "Delete everything on this device?",
        "Every session, draft, screenshot and transcript, your settings, and this device's tokens go. ScreenTail will need to be set up again. This cannot be undone.",
        "Delete everything",
        "Keep everything",
        ConfirmTone.Danger);

    /// <summary>
    /// Quitting while a session is being captured (INV-4): the app closes the recording indicator, so it
    /// stops the session first. Asked, because quitting mid-session is a choice.
    /// </summary>
    public static ConfirmWords QuitWhileCapturing { get; } = new(
        "Quit ScreenTail?",
        "A session is being captured. Quitting closes the recording indicator, so ScreenTail stops capturing first and finishes the note.",
        "Stop and quit",
        "Keep capturing",
        ConfirmTone.Primary);

    /// <summary>Spec §5 S4 bulk discard, typed with the count (v0.4.1 Q6).</summary>
    public static ConfirmWords DiscardSessions(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var sessions = count == 1 ? "1 session" : string.Create(CultureInfo.InvariantCulture, $"{count} sessions");
        return new ConfirmWords(
            $"Discard {sessions}?",
            count == 1
                ? "Its screenshots, transcript and timeline are deleted from this device straight away, and its note goes with them. This cannot be undone."
                : "Their screenshots, transcripts and timelines are deleted from this device straight away, and their notes go with them. This cannot be undone.",
            $"Discard {sessions}",
            count == 1 ? "Keep it" : "Keep them",
            ConfirmTone.Danger);
    }

    /// <summary>
    /// Spec §5 S4 Mark as published (v0.5.3, worded v0.6.11): a short question, then the session in words,
    /// then what follows. "Not yet" is the default, so Enter does not vouch for a ticket by reflex.
    /// </summary>
    public static ConfirmWords MarkAsPublished(string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        return new ConfirmWords(
            Shell.MarkAsPublished.Heading,
            $"{session}.\n\n{Shell.MarkAsPublished.Question}",
            Shell.MarkAsPublished.Action,
            "Not yet",
            ConfirmTone.Primary);
    }

    /// <summary>
    /// Settings, Integrations, Remove (v0.6.11). Asked inline on the integration's card, never in a
    /// dialog, so it can be asked while a session is being captured (Spec §4).
    /// </summary>
    public static ConfirmWords RemoveIntegration(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new ConfirmWords(
            $"Remove {title}?",
            $"Its keys are deleted from this device, and notes cannot be published to {title} until you connect it again.",
            $"Remove {title}",
            "Keep it",
            ConfirmTone.Danger);
    }

    /// <summary>The tray's discard, when the service would not do it or could not be asked: a notice, one button.</summary>
    public static ConfirmWords NotDiscarded(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ConfirmWords("The session was not discarded", reason, null, "Close", ConfirmTone.Primary);
    }
}
