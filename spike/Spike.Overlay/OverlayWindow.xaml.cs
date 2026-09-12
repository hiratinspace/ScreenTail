using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenTail.Spike.Overlay;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const uint WdaNone = 0x0;
    private const uint WdaExcludeFromCapture = 0x11; // Windows 10 2004+

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly bool _excludeAtStart;
    private IntPtr _hwnd;
    private bool _excluded;
    private string _affinityNote = string.Empty;

    public OverlayWindow(bool excludeAtStart)
    {
        _excludeAtStart = excludeAtStart;
        InitializeComponent();
        _clock.Tick += (_, _) => Render();
    }

    public void SetExcludedFromCapture(bool excluded)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var ok = SetWindowDisplayAffinity(_hwnd, excluded ? WdaExcludeFromCapture : WdaNone);
        _excluded = excluded && ok;
        _affinityNote = ok ? string.Empty : $" · affinity failed ({Marshal.GetLastWin32Error()})";
        Render();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Never take focus from the remote window, and stay out of Alt+Tab.
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;

        SetExcludedFromCapture(_excludeAtStart);
        _clock.Start();
    }

    private void Render()
    {
        var elapsed = DateTime.Now - _startedAt;
        var state = _excluded ? "excluded from capture" : "VISIBLE to capture";
        StatusText.Text = $"● {elapsed:mm\\:ss}   spike overlay · {state}{_affinityNote}";
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e) => DragMove();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
