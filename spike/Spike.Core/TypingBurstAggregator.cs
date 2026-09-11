namespace ScreenTail.Spike.Core;

/// <summary>
/// The only keyboard record that leaves the hook layer. Deliberately has no field that could hold a key (INV-2).
/// </summary>
public sealed record KeyboardEvent(string Type, int Count, long TsMs)
{
    public const string TypingBurst = "typing_burst";
    public const string Shortcut = "shortcut";
    public const string Enter = "enter";
}

/// <summary>Folds categorized key-downs into typing bursts, shortcuts and enters with counts only.</summary>
public sealed class TypingBurstAggregator
{
    private readonly long _burstGapMs;
    private int _burstCount;
    private long _burstStartMs;
    private long _lastKeyMs;

    public TypingBurstAggregator(long burstGapMs = 1500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burstGapMs, 1);
        _burstGapMs = burstGapMs;
    }

    public IReadOnlyList<KeyboardEvent> OnKey(KeyCategory category, long tsMs)
    {
        var emitted = new List<KeyboardEvent>(2);

        switch (category)
        {
            case KeyCategory.Character:
                if (_burstCount > 0 && tsMs - _lastKeyMs > _burstGapMs)
                {
                    emitted.Add(CloseBurst());
                }

                if (_burstCount == 0)
                {
                    _burstStartMs = tsMs;
                }

                _burstCount++;
                _lastKeyMs = tsMs;
                break;

            case KeyCategory.Enter:
                CloseInto(emitted);
                emitted.Add(new KeyboardEvent(KeyboardEvent.Enter, 1, tsMs));
                break;

            case KeyCategory.Shortcut:
                CloseInto(emitted);
                emitted.Add(new KeyboardEvent(KeyboardEvent.Shortcut, 1, tsMs));
                break;

            case KeyCategory.Modifier:
            case KeyCategory.Other:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        return emitted;
    }

    /// <summary>Closes an open burst once the gap has elapsed with no further typing.</summary>
    public KeyboardEvent? Poll(long nowMs) =>
        _burstCount > 0 && nowMs - _lastKeyMs > _burstGapMs ? CloseBurst() : null;

    public KeyboardEvent? Flush() => _burstCount > 0 ? CloseBurst() : null;

    private void CloseInto(List<KeyboardEvent> emitted)
    {
        if (_burstCount > 0)
        {
            emitted.Add(CloseBurst());
        }
    }

    private KeyboardEvent CloseBurst()
    {
        var burst = new KeyboardEvent(KeyboardEvent.TypingBurst, _burstCount, _burstStartMs);
        _burstCount = 0;
        return burst;
    }
}
