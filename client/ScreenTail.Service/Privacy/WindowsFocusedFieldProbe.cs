using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Privacy;

namespace ScreenTail.Service.Privacy;

/// <summary>Which of the two mechanisms answered.</summary>
internal enum FocusAnswerSource
{
    None,
    Automation,
    WindowMessage,
}

/// <summary>
/// Asks Windows whether the focused control masks what is typed into it (ST-040).
///
/// UI Automation is the only mechanism that answers for every toolkit. The Win32 way — find the focused
/// window and send it <c>EM_GETPASSWORDCHAR</c> — works for classic edit controls and fails for exactly the
/// cases that matter most: a WPF <c>PasswordBox</c> is not a window at all, and neither is a password field
/// in a browser. It is kept as a fallback for the reverse case, a control with no automation peer.
///
/// The COM interfaces below are declared by hand rather than taken from a package. Only two calls are
/// needed, and every other Windows integration in this service is hand-written interop for the same reason:
/// a process that reads everything on a technician's screen should ship as little third-party native code
/// as it can. The managed <c>System.Windows.Automation</c> wrapper would mean a framework reference to all
/// of WindowsDesktop for two method calls.
///
/// Nothing here runs on the focus hook's message pump. UIA calls cross into the focused application's
/// process and can block for as long as that application is busy; <see cref="PasswordFieldGuard"/> calls
/// this from its own loop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFocusedFieldProbe : IFocusedFieldProbe, IDisposable
{
    private const int UIA_IsPasswordPropertyId = 30019;
    private const uint EM_GETPASSWORDCHAR = 0x00D2;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private IUIAutomation? _automation;
    private bool _automationFailed;

    /// <summary>
    /// Which mechanism gave the last answer. Worth having because the two are not equivalent: the window
    /// message can only see a classic edit control, so a machine where automation has quietly stopped
    /// answering still passes every test written against a Win32 password box while being blind to the
    /// WPF and browser fields that make up most of what a technician actually sees.
    /// </summary>
    public FocusAnswerSource LastAnswerFrom { get; private set; }

    public FocusedField Read()
    {
        // UIA first: it is the only one of the two that can see a WPF PasswordBox or a browser field.
        var viaAutomation = ReadViaAutomation();
        if (viaAutomation != FocusedField.Unknown)
        {
            LastAnswerFrom = FocusAnswerSource.Automation;
            return viaAutomation;
        }

        var viaMessage = ReadViaWindowMessage();
        LastAnswerFrom = viaMessage == FocusedField.Unknown ? FocusAnswerSource.None : FocusAnswerSource.WindowMessage;
        return viaMessage;
    }

    private FocusedField ReadViaAutomation()
    {
        var automation = Automation();
        if (automation is null)
        {
            return FocusedField.Unknown;
        }

        IUIAutomationElement? element = null;
        try
        {
            element = automation.GetFocusedElement();
            if (element is null)
            {
                return FocusedField.Unknown;
            }

            // Current rather than cached: the cached tree would need a request built for it, and this is
            // asked once per focus change rather than in a loop.
            return element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is bool isPassword
                ? isPassword ? FocusedField.Password : FocusedField.NotPassword
                : FocusedField.Unknown;
        }
        catch (COMException)
        {
            // The focused application is busy, gone, or not one we may inspect. Not an answer.
            return FocusedField.Unknown;
        }
        catch (InvalidCastException)
        {
            return FocusedField.Unknown;
        }
        finally
        {
            if (element is not null)
            {
                _ = Marshal.ReleaseComObject(element);
            }
        }
    }

    /// <summary>
    /// The classic check, for a control UI Automation has no peer for. A non-zero password character means
    /// the edit control masks its content.
    /// </summary>
    private static FocusedField ReadViaWindowMessage()
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero)
        {
            return FocusedField.Unknown;
        }

        // A timeout, not a plain SendMessage: the focused window belongs to another process and may be
        // hung, and this runs on the guard's loop, not on a pump we can afford to block.
        if (SendMessageTimeout(info.Focus, EM_GETPASSWORDCHAR, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var result) == IntPtr.Zero)
        {
            return FocusedField.Unknown;
        }

        return result != IntPtr.Zero ? FocusedField.Password : FocusedField.NotPassword;
    }

    /// <summary>
    /// Created once and kept. Creating the automation object is the expensive part, and a failure to
    /// create it is permanent on a machine without UI Automation rather than worth retrying every second.
    /// </summary>
    private IUIAutomation? Automation()
    {
        if (_automation is not null || _automationFailed)
        {
            return _automation;
        }

        try
        {
            _automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(CUIAutomation)!)!;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or ArgumentNullException)
        {
            _automationFailed = true;
        }

        return _automation;
    }

    public void Dispose()
    {
        if (_automation is not null)
        {
            _ = Marshal.ReleaseComObject(_automation);
            _automation = null;
        }
    }

    private static readonly Guid CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        // The vtable order is the contract, so every method before the ones used has to be declared even
        // though it is never called. These are the first entries of IUIAutomation in UIAutomationClient.h.
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        void ElementFromHandle();

        void ElementFromPoint();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElement? GetFocusedElement();
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
