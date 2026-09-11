using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>
/// Installs WH_MOUSE_LL and WH_KEYBOARD_LL on a dedicated thread and pumps its message loop.
/// Callbacks do constant work and never allocate: time themselves, categorize, push to a ring.
/// The virtual-key code is categorized in place and never leaves the callback (INV-2).
/// </summary>
internal sealed unsafe class HookThread : IDisposable
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly LatencyStats _callbackMs;
    private readonly LatencyStats _dispatchMs;
    private readonly SpscRing<InputSample> _ring;
    private readonly Native.LowLevelProc _mouseProc;
    private readonly Native.LowLevelProc _keyboardProc;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private int _lastHookTick;
    private string? _startError;

    // Modifier state, touched only on the hook thread.
    private bool _ctrl;
    private bool _alt;
    private bool _win;

    public HookThread(LatencyStats callbackMs, LatencyStats dispatchMs, SpscRing<InputSample> ring)
    {
        _callbackMs = callbackMs;
        _dispatchMs = dispatchMs;
        _ring = ring;

        // Held in fields so the GC never collects a delegate Windows is still calling.
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
    }

    public uint LastHookTick => unchecked((uint)Volatile.Read(ref _lastHookTick));

    public void Start()
    {
        _lastHookTick = Environment.TickCount;
        _thread = new Thread(Run) { IsBackground = true, Name = "spike-hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();

        if (_startError is not null)
        {
            throw new InvalidOperationException(_startError);
        }
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        Native.PostThreadMessage(_threadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        var module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WhMouseLl, _mouseProc, module, 0);
        _keyboardHook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _keyboardProc, module, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            _startError = $"SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}.";
        }

        _ready.Set();

        if (_startError is null)
        {
            // Low-level hooks are delivered through this thread's message loop.
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }

        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var start = Stopwatch.GetTimestamp();

        if (nCode >= 0)
        {
            var data = (Native.MsLlHookStruct*)lParam;
            RecordArrival(data->Time);

            var message = (int)wParam;
            if (message is Native.WmLButtonDown or Native.WmRButtonDown or Native.WmMButtonDown)
            {
                _ring.TryWrite(new InputSample(InputKind.Click, KeyCategory.Other, data->Time, data->Pt.X, data->Pt.Y));
            }
        }

        // Only our own work counts as added latency; the rest of the chain belongs to other hooks.
        _callbackMs.Add((Stopwatch.GetTimestamp() - start) * TicksToMs);
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var start = Stopwatch.GetTimestamp();

        if (nCode >= 0)
        {
            var data = (Native.KbdLlHookStruct*)lParam;
            RecordArrival(data->Time);

            var vk = (int)data->VkCode;
            var message = (int)wParam;
            var down = message is Native.WmKeyDown or Native.WmSysKeyDown;

            if (down || message is Native.WmKeyUp or Native.WmSysKeyUp)
            {
                TrackModifier(vk, down);
            }

            if (down)
            {
                var category = KeyCategorizer.Categorize(vk, _ctrl, _alt, _win);
                _ring.TryWrite(new InputSample(InputKind.Key, category, data->Time, 0, 0));
            }
        }

        _callbackMs.Add((Stopwatch.GetTimestamp() - start) * TicksToMs);
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void RecordArrival(uint eventTick)
    {
        var now = Environment.TickCount;
        Volatile.Write(ref _lastHookTick, now);

        // Tick-count resolution (~15.6 ms) makes this a starvation indicator, not a precise latency.
        var delay = unchecked(now - (int)eventTick);
        if (delay >= 0)
        {
            _dispatchMs.Add(delay);
        }
    }

    private void TrackModifier(int vk, bool down)
    {
        switch (vk)
        {
            case 0x11 or 0xA2 or 0xA3:
                _ctrl = down;
                break;
            case 0x12 or 0xA4 or 0xA5:
                _alt = down;
                break;
            case 0x5B or 0x5C:
                _win = down;
                break;
        }
    }
}
