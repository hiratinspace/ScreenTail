using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>How a confirmed, irreversible command ended.</summary>
public enum ConfirmedOutcome
{
    /// <summary>The service could not be asked for a token, so nothing was shown and nothing was sent.</summary>
    NotAvailable,

    /// <summary>The technician did not type the phrase. Nothing was sent; the token is left to expire.</summary>
    Declined,

    /// <summary>The service did it.</summary>
    Done,

    /// <summary>The service answered and said no. <see cref="ConfirmedResult.Error"/> carries its words.</summary>
    Refused,
}

/// <param name="Error">The service's own words when it refused, or what to tell the technician when it could not be asked. Null when done or declined.</param>
public sealed record ConfirmedResult(ConfirmedOutcome Outcome, string? Error = null)
{
    public bool Done => Outcome == ConfirmedOutcome.Done;
}

/// <summary>
/// The typed-confirmation flow behind Discard and Delete everything (ST-085, Spec §3), as decisions
/// rather than as a chain of fire-and-forget calls.
///
/// Three steps, and the last one's answer matters: the service says what to type, the technician types
/// it, and the command goes back with a token issued just then. The service refuses the
/// command when a session is running or when the token is older than thirty seconds, and that answer
/// used to be dropped on the floor, so a refused "delete everything" closed its dialog exactly as a
/// successful one did. The technician believed INV-12's erase had happened and it had not (2026-10-12
/// review, S-46, S-50, T-158). Here the answer is the result, and the shell shows it.
///
/// Platform-neutral so the whole flow is tested on any machine (ADR-0002); the shell supplies the dialog.
/// </summary>
public static class ConfirmedAction
{
    public const string NotConnected = "Not connected to the capture service, so nothing was changed.";

    public const string RefusedWithoutReason = "The capture service refused, so nothing was changed.";

    public const string PhraseChanged = "The capture service now asks for different words, so nothing was changed. Try again.";

    /// <param name="issue">Asks the service for a token for the action; null when there is nobody to ask.</param>
    /// <param name="action"><c>discard_session</c> or <c>erase_everything</c>.</param>
    /// <param name="typed">
    /// Shows the phrase and says whether the technician typed it. Asynchronous so the shell can put the
    /// dialog on its own thread: the token arrives on the pipe's reading thread, and a dialog that blocked
    /// it would hold up every state change for as long as the technician took to decide.
    /// </param>
    /// <param name="build">The destructive command for a token. The request id is filled in when it is sent.</param>
    /// <param name="send">Sends it and brings back the service's answer.</param>
    public static async Task<ConfirmedResult> RunAsync(
        Func<string, CancellationToken, Task<ConfirmationIssued?>> issue,
        string action,
        Func<string, Task<bool>> typed,
        Func<string, Func<int, IpcCommand>> build,
        Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> send,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(send);

        var issued = await issue(action, ct).ConfigureAwait(false);
        if (issued is null)
        {
            // Not connected, or the service does not know this action. There is nothing to confirm, and a
            // dialog that led somewhere would be worse than none.
            return new ConfirmedResult(ConfirmedOutcome.NotAvailable, NotConnected);
        }

        if (!await typed(issued.Phrase).ConfigureAwait(false))
        {
            // Telling the service the token went unused would be a round trip to say nothing; in thirty
            // seconds it is worthless anyway.
            return new ConfirmedResult(ConfirmedOutcome.Declined);
        }

        // A fresh token, asked for once the phrase is typed. The first one's thirty seconds ran while the
        // technician read the consequence and typed, and "DELETE EVERYTHING" read with care can take longer
        // than that, which turned a considered yes into a refusal (S-46). The first token is left to expire.
        var spend = await issue(action, ct).ConfigureAwait(false);
        if (spend is null)
        {
            return new ConfirmedResult(ConfirmedOutcome.NotAvailable, NotConnected);
        }

        if (!string.Equals(spend.Phrase, issued.Phrase, StringComparison.Ordinal))
        {
            // What was typed is no longer what the service asks for. Agreeing to one sentence is not
            // agreeing to another, so nothing is sent.
            return new ConfirmedResult(ConfirmedOutcome.Refused, PhraseChanged);
        }

        var result = await send(build(spend.Token), ct).ConfigureAwait(false);
        if (result.Ok)
        {
            return new ConfirmedResult(ConfirmedOutcome.Done);
        }

        return new ConfirmedResult(
            ConfirmedOutcome.Refused,
            string.IsNullOrWhiteSpace(result.Error) ? RefusedWithoutReason : result.Error);
    }
}
