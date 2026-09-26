using ScreenTail.Core.Input;

namespace ScreenTail.Service.Input;

/// <summary>
/// The hotkeys as the service holds them across a change of bindings (ST-080). <see cref="Hotkeys"/>
/// registers a fixed set on its own thread; a new set is a new instance, released and made again one
/// change at a time, with the same handler and the conflicts read the same way.
/// </summary>
internal sealed class HotkeyHost(HotkeyBindings bindings, Action<HotkeyAction> pressed) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Hotkeys _current = Wire(new Hotkeys(bindings), pressed);

    public IReadOnlyList<HotkeyConflict> Conflicts => _current.Conflicts;

    public Task StartAsync(CancellationToken ct = default) => _current.StartAsync(ct);

    /// <summary>Releases every registration and makes them again under the new bindings. Returns the conflicts, if any.</summary>
    public async Task<IReadOnlyList<HotkeyConflict>> RebindAsync(HotkeyBindings next, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _current.DisposeAsync().ConfigureAwait(false);
            _current = Wire(new Hotkeys(next), pressed);
            await _current.StartAsync(ct).ConfigureAwait(false);
            return _current.Conflicts;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _current.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static Hotkeys Wire(Hotkeys hotkeys, Action<HotkeyAction> pressed)
    {
        hotkeys.Pressed += pressed;
        return hotkeys;
    }
}
