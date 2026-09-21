using System.Security.Cryptography;

namespace ScreenTail.Core.Ipc;

/// <summary>What a confirmation is for. A token issued for one is no use for the other.</summary>
public enum DestructiveAction
{
    /// <summary>Throw away the session being recorded, and everything captured in it.</summary>
    DiscardSession,

    /// <summary>Delete every session, every frame and every stored credential (INV-12).</summary>
    EraseEverything,
}

/// <summary>
/// Makes the service refuse a destructive command that nobody confirmed (ST-085, Spec §3).
///
/// The spec has always asked for a typed confirmation before an irreversible delete, and nothing enforced
/// it. "Discard session" sat one item below "Stop and draft" in the tray menu and threw the session away
/// on a single click; <c>erase_all_local_data</c> had no fields at all, so one frame on the pipe wiped
/// the store (2026-09-19 review).
///
/// <b>The rule belongs here rather than in the dialog.</b> A confirmation the UI is trusted to have shown
/// is a confirmation a process that is not the UI does not have to show — and the pipe only proves the
/// peer is the same user, which a compromised same-user process also is. So the service issues a token,
/// and the command has to carry it back.
///
/// Two round trips is the whole of the defence: a single crafted message can no longer do it, and a
/// token is good once, for one action, for thirty seconds, <b>and only for the connection that asked
/// for it</b>. That last part was missing, and without it the second round trip was not a round trip at
/// all — the token was a bearer token, every authenticated client shared one issuer, and the impostor
/// this class was written against could wait for the technician to confirm something of their own and
/// spend it (2026-09-20 review).
///
/// <b>What it does not do.</b> A same-user process that speaks the protocol can still ask for its own
/// token and spend it: two frames instead of one, with no person involved. The typed phrase is checked
/// in the dialog, and a program does not have to show a dialog. So this stops a single crafted message
/// and an accident, and it raises the cost of the rest; it is not a defence against a process that is
/// already running as the technician, and nothing here can be.
/// </summary>
public sealed class Confirmations(TimeProvider? time = null)
{
    /// <summary>
    /// How long a token is good for.
    ///
    /// A person reading "type DISCARD to throw away this session" and typing it takes a few seconds.
    /// Thirty leaves room for hesitating and for a slow machine, and still means a token captured from a
    /// log or a crash dump is worthless by the time anybody reads it.
    /// </summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many may be outstanding at once.
    ///
    /// Issuing was free and unbounded, and <see cref="Forget"/> walks every entry under the lock: a
    /// client in a loop bought itself megabytes of dictionary and made every honest spend wait behind
    /// the scan. A person confirms one thing at a time, so eight is already generous (2026-09-20 review).
    /// </summary>
    public const int MostOutstanding = 8;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Issued> _issued = new(StringComparer.Ordinal);

    /// <summary>A token's terms: what it is for, who may spend it, and until when.</summary>
    private readonly record struct Issued(DestructiveAction Action, Guid Caller, DateTimeOffset Expires, long Order);

    private long _order;

    /// <summary>
    /// Issues a token for one action.
    ///
    /// Cryptographic randomness, because a token a caller can predict is not a second round trip.
    ///
    /// Bound to <paramref name="caller"/>, the connection that asked. Nothing an attacker gains by asking
    /// repeatedly is worth having — what they get is a pile of tokens they already had — but asking
    /// repeatedly used to cost the service memory and lock time, so the oldest is dropped past
    /// <see cref="MostOutstanding"/>. The oldest rather than the newest: the one a technician is about
    /// to type is the one that was just issued.
    /// </summary>
    public string Issue(DestructiveAction action, Guid caller)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            Forget();
            while (_issued.Count >= MostOutstanding)
            {
                var oldest = _issued.OrderBy(entry => entry.Value.Order).First().Key;
                _ = _issued.Remove(oldest);
            }

            _issued[token] = new Issued(action, caller, _time.GetUtcNow() + GoodFor, _order++);
        }

        return token;
    }

    /// <summary>
    /// Spends a token, if it is one and it is for this action.
    ///
    /// Single use: a token that stayed valid would let one confirmed discard authorise every later one,
    /// which is the defence gone. Constant-time comparison is not needed — the lookup is by exact value
    /// and a wrong guess is rejected whatever the timing says, because there is nothing to narrow down.
    /// </summary>
    public bool Spend(string? token, DestructiveAction action, Guid caller)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            Forget();

            if (!_issued.TryGetValue(token, out var issued))
            {
                return false;
            }

            // A token offered by a connection it was not issued to is left where it is.
            //
            // Everything else about a presented token burns it, and that is right: a token that survives
            // being offered for the wrong action is one whose holder may try twice. But the holder there
            // is the window the token belongs to. Burning it for a connection that is not that window
            // hands any authenticated client a way to cancel the technician's confirmation just as they
            // are typing the phrase — a denial of service built out of the defence. Nothing is learned by
            // trying again, because the token is 128 bits of randomness and the answer never changes.
            if (issued.Caller != caller)
            {
                return false;
            }

            // Single use, from here on unconditional.
            _ = _issued.Remove(token);
            return issued.Action == action && issued.Expires > _time.GetUtcNow();
        }
    }

    /// <summary>How many are outstanding. For tests, and for a diagnostics line if one is ever wanted.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
            {
                Forget();
                return _issued.Count;
            }
        }
    }

    /// <summary>
    /// Drops what has expired.
    ///
    /// Called on every use rather than on a timer: the dictionary is only ever a handful of entries, and
    /// a background timer to clean up after a feature nobody is using is more machinery than the thing
    /// it maintains. Always under the lock.
    /// </summary>
    private void Forget()
    {
        if (_issued.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var stale in _issued.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToList())
        {
            _ = _issued.Remove(stale);
        }
    }
}
