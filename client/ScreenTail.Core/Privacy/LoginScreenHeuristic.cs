namespace ScreenTail.Core.Privacy;

/// <summary>
/// Recognises a sign-in screen from the words on it (ST-041).
///
/// Inside a remote session ScreenTail is blind to the UI: the remote desktop is one opaque bitmap, so UI
/// Automation cannot tell us a password box has focus (ADR-0001 finding 2). Reading the screen is the only
/// signal left. A frame that looks like a login prompt is marked <c>sensitive_context</c>, which suppresses
/// capture for a short while afterwards — the technician is about to type a credential into a box we
/// cannot see.
///
/// It errs towards suspicion. A false positive costs a few seconds of screenshots; a false negative puts a
/// customer's password in a note.
///
/// The frame that triggers this is still stored. A sign-in screen shows dots, not the password, and
/// keeping it is what lets the note say the technician reached the prompt. What is withheld is everything
/// for the next few seconds — which is when the typing happens.
/// </summary>
public static class LoginScreenHeuristic
{
    /// <summary>How long capture stays suppressed after a login screen is seen (ST-041).</summary>
    public static readonly TimeSpan Suppression = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Words that mean a credential is being asked for. Two of these together is the signal.
    ///
    /// The spellings are deliberately redundant. OCR returns what is drawn, so "Sign-in" arrives as one
    /// token and never matches "sign in"; and Windows says "Log On" with a space in the places that matter
    /// most — the classic logon banner an RDP session lands on, and the Services "Log On" tab, which has a
    /// real password field on it. A cue list that knows "logon" but not "log on" misses both.
    /// </summary>
    private static readonly string[] Strong =
    [
        "password", "passwort", "contraseña", "mot de passe", "passphrase", "passcode",
        "sign in", "sign-in", "signin", "log in", "log-in", "login", "log on", "log-on", "logon",
        "credentials", "authenticate",
        "unlock", "pin", "one-time code", "verification code", "two-factor", "mfa",
    ];

    /// <summary>Weaker on their own — a form asking for a name is not a login — but they add up.</summary>
    private static readonly string[] Supporting =
    [
        "username", "user name", "email", "account", "domain", "remember me",
        "forgot", "next", "continue", "submit", "ok", "cancel", "smartcard", "windows security",
    ];

    /// <param name="words">What the recogniser read. Order is irrelevant; presence is what counts.</param>
    /// <returns>True when this frame looks like somewhere a credential gets typed.</returns>
    public static bool LooksLikeLogin(IReadOnlyList<OcrWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return false;
        }

        // Joined with spaces so two-word cues ("sign in", "windows security") can be found at all.
        var text = string.Join(' ', words.Select(w => w.Text)).ToLowerInvariant();

        var strong = Strong.Count(cue => text.Contains(cue, StringComparison.Ordinal));
        if (strong == 0)
        {
            return false;
        }

        // "Password" alone is enough: on a screen we cannot inspect, that word beside a box is the whole
        // situation this exists for. Anything else needs a second cue so a help article about passwords
        // doesn't suppress a session.
        if (text.Contains("password", StringComparison.Ordinal) || text.Contains("passphrase", StringComparison.Ordinal))
        {
            return true;
        }

        var supporting = Supporting.Count(cue => text.Contains(cue, StringComparison.Ordinal));
        return strong >= 2 || (strong >= 1 && supporting >= 2);
    }

}
