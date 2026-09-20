using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Input;

/// <summary>
/// Turns buffered signals into the events a session stores (ST-024).
///
/// Aggregation lives here rather than in the hook callback for two reasons: the callback must stay trivial,
/// and a rule like "how long a pause ends a burst" is a judgement that should be readable and testable
/// without a keyboard. Typing arrives as one signal per key and leaves as one <c>typing_burst</c> with a
/// count — the count is the only thing about typing that survives (INV-2).
/// </summary>
public sealed class InputSignalReader(TimeSpan? burstGap = null)
{
    /// <summary>A pause longer than this ends a burst. A sentence typed with thinking pauses is still one burst.</summary>
    private readonly TimeSpan _burstGap = burstGap ?? TimeSpan.FromSeconds(2);
    private int _burstKeys;
    private long _burstStart;
    private long _burstLast;

    /// <summary>
    /// Converts signals into events. <paramref name="toSessionMs"/> maps a hook timestamp to milliseconds
    /// since the session started, and <paramref name="elapsed"/> measures the gap between two timestamps.
    /// </summary>
    public IEnumerable<SessionEvent> Read(
        ReadOnlyMemory<InputSignal> signals,
        Func<long, long> toSessionMs,
        Func<long, long, TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(toSessionMs);
        ArgumentNullException.ThrowIfNull(elapsed);

        var events = new List<SessionEvent>();
        for (var i = 0; i < signals.Length; i++)
        {
            var signal = signals.Span[i];
            switch (signal.Kind)
            {
                case InputKind.PrintableKey:
                    if (_burstKeys > 0 && elapsed(_burstLast, signal.Timestamp) > _burstGap)
                    {
                        events.Add(CloseBurst(toSessionMs));
                    }

                    if (_burstKeys == 0)
                    {
                        _burstStart = signal.Timestamp;
                    }

                    _burstKeys++;
                    _burstLast = signal.Timestamp;
                    break;

                case InputKind.Enter:
                    // Enter ends a burst: it is usually the moment a value was committed, and the note reads
                    // better with "typed 14 characters, pressed Enter" than with one merged blur.
                    if (_burstKeys > 0)
                    {
                        events.Add(CloseBurst(toSessionMs));
                    }

                    events.Add(new EnterEvent { TsMs = toSessionMs(signal.Timestamp) });
                    break;

                case InputKind.Shortcut:
                    if (_burstKeys > 0)
                    {
                        events.Add(CloseBurst(toSessionMs));
                    }

                    events.Add(new ShortcutEvent { TsMs = toSessionMs(signal.Timestamp) });
                    break;

                case InputKind.Click:
                    if (_burstKeys > 0)
                    {
                        events.Add(CloseBurst(toSessionMs));
                    }

                    events.Add(new ClickEvent
                    {
                        TsMs = toSessionMs(signal.Timestamp),
                        X = signal.X,
                        Y = signal.Y,
                        Button = signal.Button switch
                        {
                            MouseButtonKind.Right => MouseButton.Right,
                            MouseButtonKind.Middle => MouseButton.Middle,
                            _ => MouseButton.Left,
                        },
                    });
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(signals), signal.Kind, null);
            }
        }

        return events;
    }

    /// <summary>
    /// Forgets the burst in progress without writing it down.
    ///
    /// The counter below outlives every boundary the rest of the product respects. The hooks run for the
    /// life of the service, so left alone it goes on counting through a pause, a password field and a
    /// customer's password manager, and hands the total to whichever event next closes the burst — by
    /// which time capture is back on and nothing about the event says where its keys were typed. The
    /// caller knows when typing may not be counted; this is how it says so.
    /// </summary>
    public void Discard() => _burstKeys = 0;

    /// <summary>Emits any burst still open, for when capture stops mid-sentence.</summary>
    public SessionEvent? Flush(Func<long, long> toSessionMs)
    {
        ArgumentNullException.ThrowIfNull(toSessionMs);
        return _burstKeys > 0 ? CloseBurst(toSessionMs) : null;
    }

    private TypingBurstEvent CloseBurst(Func<long, long> toSessionMs)
    {
        var burst = new TypingBurstEvent { TsMs = toSessionMs(_burstStart), CharCount = _burstKeys };
        _burstKeys = 0;
        return burst;
    }
}
