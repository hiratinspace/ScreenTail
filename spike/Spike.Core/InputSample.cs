namespace ScreenTail.Spike.Core;

public enum InputKind : byte
{
    Click,
    Key,
}

/// <summary>
/// What the hook thread hands to the rest of the process. Carries a key category, never a key code (INV-2).
/// </summary>
/// <param name="EventTick">GetTickCount-domain timestamp from the hook struct.</param>
public readonly record struct InputSample(InputKind Kind, KeyCategory Category, uint EventTick, int X, int Y);
