using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Input;

namespace ScreenTail.Service.Input;

/// <summary>
/// Registers the global chords with Windows and reports which ones it would not give us (ST-029).
///
/// <c>RegisterHotKey</c> rather than reading chords out of ST-024's low-level keyboard hook, even though
/// that hook already sees every key. Three reasons, and the last one decides it. Windows arbitrates, so the
/// chord does not also reach whatever the technician is typing into. Failure is how a conflict is
/// discovered at all — the hook route would silently double-handle a chord another application owns. And
/// swallowing keys in a hook that sits in front of every application on a support technician's machine is a
/// far larger thing to get wrong than not having a shortcut.
///
/// <c>WM_HOTKEY</c> is posted to the thread that registered, so this owns a pump. The handler does nothing
/// but hand the action off: what follows is a state transition and a store write, and neither belongs on a
/// message pump (the same rule ST-024 set for the input hooks and ST-040 for the focus watcher).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Hotkeys : IAsyncDisposable
{
    private readonly HotkeyBindings _bindings;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<HotkeyConflict> _conflicts = [];

    /// <summary>
    /// What Windows was asked to register under each id, and for which action, written at the moment
    /// <c>RegisterHotKey</c> said yes with exactly the arguments it was given. Filled by the pump before
    /// <see cref="StartAsync"/> completes and emptied when it stops; read by the pump for every message,
    /// by <see cref="Press"/> and by <see cref="Registrations"/>.
    /// </summary>
    private readonly Dictionary<int, Registration> _registered = [];
    private Thread? _pump;
    private uint _pumpThreadId;

    public Hotkeys(HotkeyBindings? bindings = null) => _bindings = bindings ?? new HotkeyBindings();

    /// <summary>A chord was pressed. Raised on the pump thread, so handlers must not block.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>
    /// Chords that ended up with no shortcut, each with something to say and a free chord to offer
    /// (Spec §5 S5 "editable chords with conflict detection"). Populated by <see cref="StartAsync"/>.
    /// </summary>
    public IReadOnlyList<HotkeyConflict> Conflicts
    {
        get
        {
            lock (_conflicts)
            {
                return [.. _conflicts];
            }
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_pump is not null)
        {
            return Task.CompletedTask;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "ScreenTail hotkeys",
        };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
        return ready.Task.WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_pump is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pumpThreadId != 0)
        {
            _ = PostThreadMessage(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        var pump = _pump;
        _pump = null;
        await Task.Run(() => pump.Join(TimeSpan.FromSeconds(5)), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>
    /// One successful <c>RegisterHotKey</c> call: the id, the flags and the key it was given, and the
    /// action a WM_HOTKEY naming that id becomes.
    /// </summary>
    internal readonly record struct Registration(int Id, HotkeyAction Action, uint Flags, uint VirtualKey);

    /// <summary>
    /// Every registration Windows accepted, as it was made. For tests: what the flags were is a fact
    /// about the call, and a helper that computes them proves nothing about what was passed.
    /// </summary>
    internal IReadOnlyList<Registration> Registrations
    {
        get
        {
            lock (_registered)
            {
                return [.. _registered.Values];
            }
        }
    }

    /// <summary>
    /// Strikes a chord without a keyboard: finds the registration Windows holds for these modifiers and
    /// this key, and posts to this pump the message Windows posts when the chord is struck, so a test can
    /// check which action each chord becomes and that it becomes it once. By chord rather than by
    /// action, because a lookup by action would go through the same mapping it is meant to check. False
    /// when no registration holds the chord or the pump is not running. For tests; the product has no
    /// reason to press its own chords.
    /// </summary>
    internal bool Press(Hotkey chord)
    {
        if (chord.VirtualKey is not { } key)
        {
            return false;
        }

        var id = 0;
        lock (_registered)
        {
            foreach (var registration in _registered.Values)
            {
                if (registration.VirtualKey == (uint)key && (registration.Flags & ~MOD_NOREPEAT) == (uint)chord.Modifiers)
                {
                    id = registration.Id;
                    break;
                }
            }
        }

        var thread = _pumpThreadId;
        if (id == 0 || thread == 0 || _pump is null)
        {
            return false;
        }

        // What Windows puts in the message: the modifiers in the low word and the key in the high word.
        var lParam = ((long)key << 16) | (uint)chord.Modifiers;
        return PostThreadMessage(thread, WM_HOTKEY, new IntPtr(id), new IntPtr(lParam));
    }

    /// <summary>
    /// The modifier flags a chord is registered with. Always includes MOD_NOREPEAT: holding the chord
    /// down is one press. Without it, leaning on Ctrl+Alt+M would mark a moment every few milliseconds
    /// for as long as the keys were held.
    /// </summary>
    private static uint RegistrationFlags(Hotkey hotkey) => (uint)hotkey.Modifiers | MOD_NOREPEAT;

    /// <summary>
    /// Asks Windows for one chord and, when it says yes, writes down exactly what it was asked, so what
    /// <see cref="Registrations"/> reports is the call and not a second computation of it.
    /// </summary>
    private bool Register(int id, HotkeyAction action, uint flags, uint virtualKey)
    {
        if (!RegisterHotKey(IntPtr.Zero, id, flags, virtualKey))
        {
            return false;
        }

        lock (_registered)
        {
            _registered[id] = new Registration(id, action, flags, virtualKey);
        }

        return true;
    }

    private void Pump(TaskCompletionSource ready)
    {
        try
        {
            _pumpThreadId = GetCurrentThreadId();

            // Anything wrong with the bindings themselves is known before Windows is asked, so Settings can
            // show every problem at once rather than one per failed attempt.
            lock (_conflicts)
            {
                _conflicts.AddRange(_bindings.Validate());
            }

            var id = 1;
            foreach (var (action, hotkey) in _bindings.All)
            {
                if (hotkey.VirtualKey is not { } key || !hotkey.IsUsable)
                {
                    continue;   // already reported by Validate
                }

                if (Register(id, action, RegistrationFlags(hotkey), (uint)key))
                {
                    id++;
                }
                else
                {
                    lock (_conflicts)
                    {
                        _conflicts.Add(_bindings.Refused(action, hotkey));
                    }
                }
            }

            ready.TrySetResult();

            while (!_stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WM_HOTKEY && TryActionFor(message.WParam.ToInt32(), out var action))
                {
                    Pressed?.Invoke(action);
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
        finally
        {
            lock (_registered)
            {
                foreach (var id in _registered.Keys)
                {
                    _ = UnregisterHotKey(IntPtr.Zero, id);
                }

                _registered.Clear();
            }

            ready.TrySetResult();
        }
    }

    private bool TryActionFor(int id, out HotkeyAction action)
    {
        lock (_registered)
        {
            var found = _registered.TryGetValue(id, out var registration);
            action = registration.Action;
            return found;
        }
    }

    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);
}
