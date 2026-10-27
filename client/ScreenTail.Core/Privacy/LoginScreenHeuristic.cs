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
    /// The password word itself, in the languages a technician's remote sessions land in. Any one of
    /// these on its own is enough: on a screen we cannot inspect, that word beside a box is the whole
    /// situation this exists for, and a German or French prompt is no less a prompt. Only the English
    /// words used to count alone, so "Kennwort" above a box never suppressed anything (2026-10-12
    /// review, S-11). Windows says Kennwort and most German web forms say Passwort, so both are here.
    /// </summary>
    private static readonly string[] PasswordWords =
    [
        "password", "passphrase",
        "passwort", "kennwort",
        "mot de passe",
        "contraseña",
        "senha",
        "wachtwoord",
        "hasło",
        "lösenord",
    ];

    /// <summary>
    /// Words that mean a credential is being asked for. Two of these together is the signal.
    ///
    /// The spellings are deliberately redundant. OCR returns what is drawn, so "Sign-in" arrives as one
    /// token and never matches "sign in"; and Windows says "Log On" with a space in the places that matter
    /// most — the classic logon banner an RDP session lands on, and the Services "Log On" tab, which has a
    /// real password field on it. A cue list that knows "logon" but not "log on" misses both.
    ///
    /// The localised verbs are the ones Windows, Microsoft 365, Google and Okta draw on their sign-in
    /// and unlock screens in German, French, Spanish and Dutch, so a credential prompt inside a remote
    /// session to a customer in those languages is scored the way an English one is.
    /// </summary>
    private static readonly string[] Strong =
    [
        .. PasswordWords,
        "passcode",
        "sign in", "sign-in", "signin", "log in", "log-in", "login", "log on", "log-on", "logon",
        "credentials", "authenticate",
        "unlock", "pin", "one-time code", "verification code", "two-factor", "mfa",
        "anmelden", "anmeldung", "entsperren", "bestätigungscode", "einmalkennwort", "anmeldedaten",
        "se connecter", "connexion", "s'identifier", "déverrouiller", "code de vérification", "identifiants",
        "iniciar sesión", "acceder", "desbloquear", "código de verificación", "credenciales",
        "aanmelden", "inloggen", "ontgrendelen", "verificatiecode",
    ];

    /// <summary>
    /// Weaker on their own, since a form asking for a name is not a login, but they add up. The localised
    /// words are the labels and buttons that sit beside a password box in the same four languages.
    /// </summary>
    private static readonly string[] Supporting =
    [
        "username", "user name", "email", "account", "domain", "remember me",
        "forgot", "next", "continue", "submit", "ok", "cancel", "smartcard", "windows security",
        "benutzername", "benutzer", "e-mail", "konto", "domäne", "angemeldet bleiben", "vergessen", "weiter", "abbrechen",
        "utilisateur", "compte", "domaine", "rester connecté", "oublié", "suivant", "annuler",
        "usuario", "cuenta", "dominio", "siguiente", "cancelar",
        "gebruikersnaam", "volgende", "annuleren",
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

        // Joined with spaces so two-word cues ("sign in", "windows security") can be found at all, and
        // padded at both ends so a cue at the very start or end still has a boundary beside it.
        var text = " " + string.Join(' ', words.Select(w => w.Text)).ToLowerInvariant() + " ";

        var strong = Strong.Count(cue => Mentions(text, cue));
        if (strong == 0)
        {
            return false;
        }

        // The password word alone is enough, in any of its languages: on a screen we cannot inspect, that
        // word beside a box is the whole situation this exists for. Anything else needs a second cue so a
        // help article about signing in doesn't suppress a session.
        if (PasswordWords.Any(cue => Mentions(text, cue)))
        {
            return true;
        }

        var supporting = Supporting.Count(cue => Mentions(text, cue));
        return strong >= 2 || (strong >= 1 && supporting >= 2);
    }

    /// <summary>
    /// Whether the screen mentions this cue as a word, rather than as letters inside another one.
    ///
    /// Substring matching put "pin" inside "shipping" and "mapping", "ok" inside "token" and "broken",
    /// and "next", "account" and "email" are ordinary words in their own right. A customer's orders page
    /// scored two supporting cues and a strong one and suppressed capture for ten seconds — failing safe,
    /// but blanking the screenshots at exactly the step the technician was documenting.
    ///
    /// The cues are lower-case and may contain spaces and hyphens ("sign-in", "one-time code"), so the
    /// boundary test is "not a letter or a digit on either side" rather than a regex word boundary, which
    /// would split the multi-word cues in the middle.
    /// </summary>
    private static bool Mentions(string text, string cue)
    {
        var at = text.IndexOf(cue, StringComparison.Ordinal);
        while (at >= 0)
        {
            var before = text[at - 1];
            var after = text[at + cue.Length];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
            {
                return true;
            }

            at = text.IndexOf(cue, at + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
