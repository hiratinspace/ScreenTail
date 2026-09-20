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
/// Two round trips is the whole of the defence, and it is enough: a single crafted message can no longer
/// do it, and a token is good once, for one action, for thirty seconds. Long enough for a person to read
/// a sentence and type a word, short enough that one left lying around is not a key to the store.
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

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DestructiveAction Action, DateTimeOffset Expires)> _issued = new(StringComparer.Ordinal);

    /// <summary>
    /// Issues a token for one action.
    ///
    /// Cryptographic randomness, because a token a caller can predict is not a second round trip. Issuing
    /// one costs nothing and destroys nothing, so there is no rate limit here: what an attacker gains by
    /// asking repeatedly is a pile of tokens they already had.
    /// </summary>
    public string Issue(DestructiveAction action)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            Forget();
            _issued[token] = (action, _time.GetUtcNow() + GoodFor);
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
    public bool Spend(string? token, DestructiveAction action)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            Forget();

            // Removed the moment it is presented, whatever it turns out to be for. Single use has to be
            // unconditional to be worth reasoning about: a token that survives being offered for the
            // wrong action is one an attacker holding it may try twice.
            if (!_issued.Remove(token, out var issued))
            {
                return false;
            }

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
