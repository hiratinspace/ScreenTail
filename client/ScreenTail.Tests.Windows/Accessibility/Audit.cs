using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The rules Spec §7 sets, checked on a real, shown WPF window (ST-084). Every method runs on the window's
/// thread and returns findings as sentences; a test asserts there are none.
///
/// What a finding says about an element is its kind and its automation name, never a window title: the
/// suite runs on the laptop too, where its output is uploaded (INV-10 holds for the suite's own logs).
/// </summary>
internal static partial class Audit
{
    /// <summary>Spec §7: the smallest thing a pointer has to hit. The HUD's controls are 32.</summary>
    public const double HitTarget = 28;

    public const double HudHitTarget = 32;

    /// <summary>Every element of the window's visual tree, depth first, in tree order.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>
    /// A control a person operates: a button, a field, a choice, a list, a slider, a splitter. Template
    /// parts of another control (a combo box's arrow, a slider's track buttons, a scroll bar's thumb) are
    /// that control, not controls of their own.
    /// </summary>
    public static bool IsInteractive(DependencyObject element) =>
        element is Control control
        && control.IsVisible
        && control is ButtonBase or TextBoxBase or PasswordBox or Selector or Slider or GridSplitter or ListBoxItem or DataGridCell
        && !IsPart(control);

    public static IReadOnlyList<Control> Interactive(Window window) =>
        [.. Descendants(window).Where(IsInteractive).Cast<Control>()];

    private static bool IsPart(Control control) =>
        control.TemplatedParent is Control owner
        && (owner is ComboBox or Slider or ScrollBar or ScrollViewer or TextBoxBase || owner.Focusable);

    /// <summary>What a finding calls an element: its kind and what a screen reader says for it.</summary>
    public static string Describe(DependencyObject element)
    {
        var kind = element.GetType().Name;
        if (element is UIElement ui && UIElementAutomationPeer.CreatePeerForElement(ui) is { } peer && element is not Window)
        {
            var name = peer.GetName();
            return string.IsNullOrEmpty(name) ? $"{kind} (no name)" : $"{kind} \"{Shorten(name)}\"";
        }

        return kind;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    // ---------------------------------------------------------------------------------------------------
    // Names and roles
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spec §7: every interactive element has a name, and lists say what each item is. Walks the automation
    /// tree a screen reader reads (the peers), not the XAML: a binding that came back empty, a list item
    /// read as its type name, or an icon glyph read as a private-use character all fail here and passed
    /// the static XAML test.
    /// </summary>
    public static List<string> Names(Window window)
    {
        var findings = new List<string>();
        var root = UIElementAutomationPeer.CreatePeerForElement(window)
            ?? throw new InvalidOperationException("The window has no automation peer.");
        foreach (var peer in Peers(root))
        {
            var type = peer.GetAutomationControlType();
            var name = peer.GetName() ?? string.Empty;
            var where = $"{peer.GetClassName()} ({peer.GetLocalizedControlType()})";
            if (peer.IsOffscreen() && type is not AutomationControlType.ListItem and not AutomationControlType.DataItem)
            {
                continue;
            }

            // A part of another control's template (a slider's or a scroll bar's track buttons) is that
            // control, which carries the name.
            if (peer is UIElementAutomationPeer { Owner: Control part } && IsPart(part))
            {
                continue;
            }

            if (NeedsName(type) && string.IsNullOrWhiteSpace(name))
            {
                findings.Add($"{where} has no name");
                continue;
            }

            if (name.Length == 0)
            {
                continue;
            }

            if (LooksLikeCode(peer, name))
            {
                findings.Add($"{where} is read as \"{Shorten(name)}\", which is a type or a record, not words");
            }

            if (name.Any(c => c is >= '' and <= ''))
            {
                findings.Add($"{where} is read as an icon font's private-use character (\\u{(int)name.First(c => c is >= '' and <= ''):X4}); hide the glyph from automation or name it");
            }

            if (name.Length > 512)
            {
                findings.Add($"{where} has a name of {name.Length} characters");
            }
        }

        // A live region on an element with no automation peer (a Border, a ContentControl, a panel) is
        // announced to nobody: the event is raised on the peer.
        foreach (var region in Descendants(window).OfType<UIElement>().Where(e => UI.Controls.LiveRegion.GetSetting(e) != AutomationLiveSetting.Off))
        {
            if (UIElementAutomationPeer.CreatePeerForElement(region) is null)
            {
                findings.Add($"a live region on a {region.GetType().Name} has no automation peer, so no screen reader hears it");
            }
        }

        foreach (var control in Interactive(window))
        {
            if (UIElementAutomationPeer.CreatePeerForElement(control) is not { } peer)
            {
                findings.Add($"{control.GetType().Name} has no automation peer, so a screen reader cannot see it");
                continue;
            }

            if (Role(control) is { } expected && peer.GetAutomationControlType() != expected)
            {
                findings.Add($"{Describe(control)} is exposed as {peer.GetAutomationControlType()}, not {expected}");
            }

            if (Pattern(control) is { } pattern && peer.GetPattern(pattern) is null)
            {
                findings.Add($"{Describe(control)} does not support the {pattern} pattern its role needs");
            }

            if (LabelMissingFromName(control, peer.GetName()) is { } label)
            {
                findings.Add($"{Describe(control)} shows \"{label}\" but is not named with it (WCAG 2.5.3, label in name)");
            }
        }

        return findings;
    }

    /// <summary>The automation tree under <paramref name="root"/>, as UI Automation would walk it.</summary>
    public static IEnumerable<AutomationPeer> Peers(AutomationPeer root)
    {
        foreach (var child in root.GetChildren() ?? [])
        {
            yield return child;
            foreach (var below in Peers(child))
            {
                yield return below;
            }
        }
    }

    private static bool NeedsName(AutomationControlType type) => type is
        AutomationControlType.Button or AutomationControlType.CheckBox or AutomationControlType.ComboBox
        or AutomationControlType.Edit or AutomationControlType.Hyperlink or AutomationControlType.ListItem
        or AutomationControlType.DataItem or AutomationControlType.MenuItem or AutomationControlType.RadioButton
        or AutomationControlType.Slider or AutomationControlType.Spinner or AutomationControlType.SplitButton
        or AutomationControlType.TabItem or AutomationControlType.TreeItem or AutomationControlType.List
        or AutomationControlType.DataGrid or AutomationControlType.Image or AutomationControlType.HeaderItem
        or AutomationControlType.ProgressBar;

    [GeneratedRegex(@"^[A-Za-z_][\w.]* \{ .*=.* \}$|^ScreenTail\.|^System\.|^MS\.Internal\.", RegexOptions.Singleline)]
    private static partial Regex CodeLike();

    private static bool LooksLikeCode(AutomationPeer peer, string name)
    {
        if (CodeLike().IsMatch(name))
        {
            return true;
        }

        return peer is ItemAutomationPeer { Item: { } item } && !(item is string) && name == item.GetType().Name;
    }

    /// <summary>The automation role each kind of control must have, so Narrator says what it is.</summary>
    private static AutomationControlType? Role(Control control) => control switch
    {
        CheckBox => AutomationControlType.CheckBox,
        RadioButton => AutomationControlType.RadioButton,
        ComboBox => AutomationControlType.ComboBox,
        ListBoxItem => AutomationControlType.ListItem,
        ListBox => AutomationControlType.List,
        DataGrid => AutomationControlType.DataGrid,
        TextBoxBase or PasswordBox => AutomationControlType.Edit,
        Slider => AutomationControlType.Slider,
        GridSplitter => AutomationControlType.Thumb,
        DataGridColumnHeader => AutomationControlType.HeaderItem,
        ButtonBase => AutomationControlType.Button,
        _ => null,
    };

    private static PatternInterface? Pattern(Control control) => control switch
    {
        // A list item's own peer is a wrapper; the selection pattern is its item peer's, which the
        // Accessibility Insights scan checks.
        ListBoxItem => null,
        RadioButton => PatternInterface.SelectionItem,
        ToggleButton => PatternInterface.Toggle,
        ComboBox => PatternInterface.ExpandCollapse,
        TextBox or PasswordBox => PatternInterface.Value,
        Slider => PatternInterface.RangeValue,
        DataGridColumnHeader => null,
        ButtonBase => PatternInterface.Invoke,
        _ => null,
    };

    /// <summary>The words a control shows, when it shows only words, and they are missing from its name.</summary>
    private static string? LabelMissingFromName(Control control, string name)
    {
        var shown = control switch
        {
            ContentControl { Content: string text } => text,
            ContentControl { Content: TextBlock block } => block.Text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(shown))
        {
            return null;
        }

        return Words(name).Contains(Words(shown), StringComparison.Ordinal) ? null : shown.Trim();
    }

    /// <summary>Lower case, "&amp;" as "and", everything that is not a letter or a digit as one space.</summary>
    private static string Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("&", " and ", StringComparison.Ordinal).ToLower(CultureInfo.InvariantCulture))
        {
            _ = builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------------------------------------------------------------------------------------------------
    // Hit targets
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Where the element is drawn, in the window's device-independent pixels.</summary>
    public static Rect Bounds(UIElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>
    /// Spec §7: every interactive element is at least <paramref name="minimum"/> device-independent pixels
    /// in each direction, measured as drawn, so a margin or a clip that shrinks it counts. List items too:
    /// a row is what the pointer hits.
    /// </summary>
    public static List<string> HitTargets(Window window, double minimum = HitTarget)
    {
        var findings = new List<string>();
        // Disabled ones too: they are enabled the moment their reason goes, and are the same size then.
        foreach (var control in Interactive(window))
        {
            var size = Bounds(control, window);
            if (size.Width + 0.5 < minimum || size.Height + 0.5 < minimum)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"{Describe(control)} is {size.Width:0.#} x {size.Height:0.#}, under {minimum} x {minimum}"));
            }
        }

        return findings;
    }

    /// <summary>The scroll bars a person can see in the window, vertical and horizontal.</summary>
    public static IReadOnlyList<ScrollBar> ScrollBars(Window window) =>
        [.. Descendants(window).OfType<ScrollBar>().Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0)];

    /// <summary>
    /// Nothing lies over a visible scroll bar: along its track, the pointer reaches the bar. Scroll bars
    /// are not Tab stops, so the keyboard checks never look at them, and a splitter reaching over a
    /// pane's edge once took the drag that should have scrolled the pane (ST-084 review).
    /// </summary>
    public static List<string> CoveredScrollBars(Window window)
    {
        var findings = new List<string>();
        foreach (var bar in ScrollBars(window))
        {
            var bounds = Bounds(bar, window);
            for (var step = 1; step <= 9; step += 2)
            {
                var point = bar.Orientation == Orientation.Vertical
                    ? new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height * step / 10))
                    : new Point(bounds.Left + (bounds.Width * step / 10), bounds.Top + (bounds.Height / 2));
                if (!Client(window).Contains(point))
                {
                    continue;
                }

                if (Hit(window, point) is { } hit && !ReferenceEquals(hit, bar) && !hit.IsDescendantOf(bar))
                {
                    var owner = bar.TemplatedParent is DependencyObject scroller ? Ancestor<Control>(scroller) ?? scroller : bar;
                    findings.Add(string.Create(CultureInfo.InvariantCulture, $"the scroll bar of {Describe(owner)} is covered at {point.X:0},{point.Y:0} by {Describe(hit)} in {Lineage(hit)}"));
                    break;
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// A button nobody can reach from the keyboard: not focusable, and not part of a control that is. Every
    /// action has to be reachable without a mouse (Spec §4).
    /// </summary>
    public static List<string> MouseOnly(Window window) =>
        [.. Descendants(window)
            .OfType<ButtonBase>()
            .Where(b => b.IsVisible && b.IsEnabled && !b.Focusable && !IsPart(b))
            .Select(b => $"{Describe(b)} can be clicked but not reached by the keyboard")];

    // ---------------------------------------------------------------------------------------------------
    // Keyboard
    // ---------------------------------------------------------------------------------------------------

    /// <summary>What Tab did on one screen: where focus went, in order, and what was wrong on the way.</summary>
    public sealed record Traversal(IReadOnlyList<UIElement> Stops, List<string> Findings);

    /// <summary>
    /// Tabs through the window from the top until focus comes back to where it started, then Shift+Tabs
    /// round again (Spec §4, §7). On the way it checks:
    /// <list type="bullet">
    /// <item>no keyboard trap: every Tab moves focus, and the round closes;</item>
    /// <item>every interactive element the window shows is reached (a list counts as reached through any of
    /// its items, which the arrows move between);</item>
    /// <item>focus follows the reading order: top to bottom, and left to right along a row;</item>
    /// <item>focus never lands on something hidden behind another element or outside the window;</item>
    /// <item>the focused element looks focused: drawn again without focus, it changes, with at least 3:1
    /// contrast between the two (WCAG 2.4.7 and 1.4.11);</item>
    /// <item>Shift+Tab goes round the same stops in reverse.</item>
    /// </list>
    /// </summary>
    /// <param name="focusVisualsOnly">
    /// Only the focus-visual check, for a second palette: what Tab reaches, in what order and whether it is
    /// covered does not change with the colours, and was checked in the first.
    /// </param>
    public static async Task<Traversal> TabAsync(Window window, bool focusVisuals = true, bool focusVisualsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var findings = new List<string>();
        var stops = new List<UIElement>();
        Keyboard.ClearFocus();
        await Settle();

        UIElement? first = null;
        for (var i = 0; i < 400; i++)
        {
            var before = Keyboard.FocusedElement;
            _ = Keys.Press(window, Key.Tab);
            await Settle();
            if (Keyboard.FocusedElement is not UIElement now || !IsWithin(now, window))
            {
                findings.Add(i == 0
                    ? "Tab put keyboard focus nowhere in the window: nothing can be reached"
                    : $"Tab from {Describe((DependencyObject)before!)} left the window or put focus nowhere");
                break;
            }

            // Focus that does not move is a trap, unless there is nowhere else for it to go: a window
            // whose only enabled control is this one.
            if (ReferenceEquals(now, before))
            {
                if (Interactive(window).Any(c => !ReferenceEquals(c, now) && c.IsEnabled && c.Focusable && !now.IsDescendantOf(c) && !c.IsDescendantOf(now)))
                {
                    findings.Add($"Tab does not move focus off {Describe(now)}: a keyboard trap");
                }

                break;
            }

            // Back at the first stop is the round closing.
            if (ReferenceEquals(now, first))
            {
                break;
            }

            if (stops.Contains(now))
            {
                findings.Add($"Tab goes round a loop that never returns to the first stop, entered at {Describe(now)}: a keyboard trap");
                break;
            }

            first ??= now;
            stops.Add(now);
            if (!focusVisualsOnly)
            {
                Visible(window, now, findings);
            }

            if (focusVisuals)
            {
                FocusShows(window, now, findings);
            }
        }

        if (!focusVisualsOnly)
        {
            Reached(window, stops, findings);
            ReadingOrder(window, stops, findings);
            await ReverseAsync(window, stops, findings);
        }

        return new Traversal(stops, findings);
    }

    private static async Task ReverseAsync(Window window, List<UIElement> stops, List<string> findings)
    {
        if (stops.Count < 2)
        {
            return;
        }

        _ = stops[0].Focus();
        await Settle();
        var back = new List<UIElement>();
        for (var i = 0; i < stops.Count; i++)
        {
            _ = Keys.Press(window, Key.Tab, ModifierKeys.Shift);
            await Settle();
            if (Keyboard.FocusedElement is UIElement now)
            {
                back.Add(now);
            }
        }

        var expected = Enumerable.Range(0, stops.Count).Select(i => stops[(stops.Count - 1 - i + stops.Count) % stops.Count]).ToList();
        for (var i = 0; i < expected.Count; i++)
        {
            if (i >= back.Count || !ReferenceEquals(back[i], expected[i]))
            {
                var got = i < back.Count ? Describe(back[i]) : "nowhere";
                findings.Add($"Shift+Tab does not retrace Tab: press {i + 1} went to {got}, not {Describe(expected[i])}");
                return;
            }
        }
    }

    private static bool IsWithin(DependencyObject element, Window window) =>
        element is Visual visual && (ReferenceEquals(visual, window) || visual.IsDescendantOf(window));

    /// <summary>The window's client area, where its content is drawn, in its own coordinates.</summary>
    public static Rect Client(Window window) =>
        new(((UIElement)VisualTreeHelper.GetChild(window, 0)).RenderSize);

    /// <summary>
    /// The focused element is inside the window, and nothing else is drawn over its centre: a hit there
    /// lands on it, on something inside it, or (where it has no background of its own) on something it
    /// sits in. Anything else is a cover, like a stale overlay.
    /// </summary>
    private static void Visible(Window window, UIElement element, List<string> findings)
    {
        var bounds = Bounds(element, window);
        var centre = new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
        if (!Client(window).Contains(centre))
        {
            findings.Add($"focus went to {Describe(element)}, which is outside the window");
            return;
        }

        if (Hit(window, centre) is { } hit
            && !ReferenceEquals(hit, element)
            && !hit.IsDescendantOf(element)
            && !element.IsDescendantOf(hit))
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"focus went to {Describe(element)} at {centre.X:0},{centre.Y:0}, which is covered by {Describe(hit)} in {Lineage(hit)}"));
        }
    }

    /// <summary>
    /// The topmost visual the pointer would reach at <paramref name="point"/>. Elements that are not
    /// visible or not hit-testable are passed over with everything inside them, as input passes over them.
    /// </summary>
    private static Visual? Hit(Window window, Point point)
    {
        Visual? found = null;
        VisualTreeHelper.HitTest(
            window,
            target => target is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                found = result.VisualHit as Visual;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(point));
        return found;
    }

    /// <summary>The kinds of the element's first few ancestors, for a finding that has to say where something is.</summary>
    private static string Lineage(DependencyObject element)
    {
        var kinds = new List<string>();
        for (var at = VisualTreeHelper.GetParent(element); at is not null && kinds.Count < 6; at = VisualTreeHelper.GetParent(at))
        {
            kinds.Add(at is FrameworkElement { Name.Length: > 0 } named ? $"{at.GetType().Name}#{named.Name}" : at.GetType().Name);
        }

        return string.Join(" < ", kinds);
    }

    /// <summary>
    /// Every interactive element the window shows is a stop, or contains one: a list or a table is reached
    /// through the item the arrows then move from, and a disabled control is not expected to be reached.
    /// </summary>
    private static void Reached(Window window, List<UIElement> stops, List<string> findings)
    {
        foreach (var control in Interactive(window))
        {
            // Taken out of the Tab order on purpose or not, a control a person operates is expected to be
            // reached: IsTabStop="False" on a button is a mouse-only button.
            if (!control.IsEnabled)
            {
                continue;
            }

            // Items are reached by the arrows from whichever one Tab lands on.
            if (ItemsControl.ItemsControlFromItemContainer(control) is not null || control is DataGridCell || Ancestor<DataGrid>(control) is not null && control is not DataGrid)
            {
                continue;
            }

            if (Ancestor<Popup>(control) is not null)
            {
                continue;
            }

            if (!stops.Any(s => ReferenceEquals(s, control) || s.IsDescendantOf(control)))
            {
                findings.Add($"{Describe(control)} is never reached by Tab");
            }
        }
    }

    /// <summary>
    /// Focus follows the reading order. For each step from one stop to the next, the two are compared at
    /// the level where their branches part: the branch focus moves into must not lie wholly above the one
    /// it left, nor wholly to its left on the same row. Branches that overlap both ways (an overlay, a
    /// column beside a header) set no order.
    /// </summary>
    private static void ReadingOrder(Window window, List<UIElement> stops, List<string> findings)
    {
        for (var i = 1; i < stops.Count; i++)
        {
            var (from, to) = Branches(stops[i - 1], stops[i]);
            if (from is null || to is null)
            {
                continue;
            }

            var a = Bounds(from, window);
            var b = Bounds(to, window);
            const double slack = 1;
            var above = b.Bottom <= a.Top + slack;
            var sameRow = b.Top < a.Bottom - slack && a.Top < b.Bottom - slack;
            var leftOf = b.Right <= a.Left + slack;
            if (above || (sameRow && leftOf))
            {
                findings.Add($"focus goes from {Describe(stops[i - 1])} to {Describe(stops[i])}, which is {(above ? "above" : "to the left of")} it: Tab order does not follow the reading order");
            }
        }
    }

    /// <summary>The children of the two stops' nearest common ancestor that hold each of them.</summary>
    private static (UIElement? From, UIElement? To) Branches(UIElement a, UIElement b)
    {
        var pathA = Path(a);
        var pathB = Path(b);
        var common = 0;
        while (common < pathA.Count && common < pathB.Count && ReferenceEquals(pathA[common], pathB[common]))
        {
            common++;
        }

        if (common >= pathA.Count || common >= pathB.Count)
        {
            return (null, null);
        }

        return (pathA[common] as UIElement, pathB[common] as UIElement);
    }

    private static List<DependencyObject> Path(DependencyObject element)
    {
        var path = new List<DependencyObject>();
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            path.Add(at);
        }

        path.Reverse();
        return path;
    }

    public static T? Ancestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element); at is not null; at = VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The focused element drawn, then drawn again with keyboard focus taken away and nothing else changed:
    /// the two must differ around it, by enough pixels to be a ring or a border and with at least 3:1
    /// contrast between before and after. Then focus is put back so Tab carries on from here.
    /// </summary>
    private static void FocusShows(Window window, UIElement element, List<string> findings)
    {
        var region = Bounds(element, window);
        region.Inflate(6, 6);
        region.Intersect(Client(window));
        if (region.IsEmpty || region.Width < 1 || region.Height < 1)
        {
            return;
        }

        // Layout only between the two pictures, not a turn of the dispatcher: losing focus takes the ring
        // and the focus triggers away at once, and a turn per picture was most of the suite's time.
        var focused = Render(window, region);
        Keyboard.ClearFocus();
        window.UpdateLayout();
        var unfocused = Render(window, region);
        _ = element.Focus();
        window.UpdateLayout();

        var changed = 0;
        var different = 0;
        for (var i = 0; i < focused.Length; i += 4)
        {
            var contrast = Contrast(focused, unfocused, i);
            if (contrast >= 3.0)
            {
                changed++;
            }

            if (contrast > 1.05)
            {
                different++;
            }
        }

        // A 2 px ring round the element, or a border going from 1 to 2 px in the accent, changes about
        // twice its perimeter; a quarter of the perimeter is a floor nothing invisible reaches.
        var needed = Math.Max(16, (int)((region.Width + region.Height) / 2));
        if (changed < needed)
        {
            findings.Add($"{Describe(element)} does not look focused: {changed} pixels around it change with 3:1 contrast when it takes keyboard focus ({different} change at all), under {needed}");
        }
    }

    /// <summary>The window's pixels in <paramref name="region"/>, as BGRA at 96 dpi, adorners (the focus ring) included.</summary>
    private static byte[] Render(Window window, Rect region)
    {
        var root = (Visual)VisualTreeHelper.GetChild(window, 0);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var brush = new VisualBrush(root)
            {
                Viewbox = new Rect(region.X, region.Y, region.Width, region.Height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, region.Width, region.Height));
        }

        var width = (int)Math.Ceiling(region.Width);
        var height = (int)Math.Ceiling(region.Height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static double Contrast(byte[] a, byte[] b, int at)
    {
        var la = Luminance(a[at + 2], a[at + 1], a[at]);
        var lb = Luminance(b[at + 2], b[at + 1], b[at]);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(byte r, byte g, byte b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    /// <summary>Lets input, data binding, layout and rendering catch up.</summary>
    public static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
        {
            foreach (Window open in Application.Current.Windows)
            {
                open.UpdateLayout();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Motion and high contrast
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// With reduce motion on, nothing moves: no element has an animated property and no popup animates
    /// open (Spec §2.3, §7).
    /// </summary>
    public static List<string> Moving(Window window)
    {
        var findings = new List<string>();
        foreach (var element in Descendants(window).Prepend(window))
        {
            // The text caret blinks by an animation of its own. Blinking is not motion, and its rate and
            // whether it blinks at all are Windows' own keyboard settings (Settings, Accessibility, Text
            // cursor), so it is the one animation this rule leaves to the system.
            if (element is UIElement { HasAnimatedProperties: true } moving && moving.GetType().Name != "CaretSubElement")
            {
                findings.Add($"{Describe(moving)} is animating with reduce motion on");
            }

            if (element is Popup { PopupAnimation: not PopupAnimation.None } popup && popup.AllowsTransparency)
            {
                findings.Add($"a popup under {Describe(VisualTreeHelper.GetParent(popup) ?? popup)} animates open ({popup.PopupAnimation}) with reduce motion on");
            }
        }

        return findings;
    }

    /// <summary>
    /// No style or template anywhere in the app starts a storyboard. A storyboard declared in XAML runs
    /// whatever Windows' animation setting says; motion has to start from code, through
    /// <see cref="UI.Theme.ThemeManager.Motion"/>, which is zero under reduce motion.
    /// </summary>
    public static List<string> Storyboards(ResourceDictionary resources, Window window)
    {
        var findings = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (key, value) in Resources(resources).Concat(Resources(window.Resources)))
        {
            Inspect(value, $"resource {key}", findings, seen);
        }

        foreach (var element in Descendants(window).Prepend(window).OfType<FrameworkElement>())
        {
            Inspect(element.Style, $"the style of {Describe(element)}", findings, seen);
            if (element is Control { Template: { } template })
            {
                Inspect(template, $"the template of {Describe(element)}", findings, seen);
            }

            foreach (var trigger in element.Triggers)
            {
                Inspect(trigger, $"a trigger on {Describe(element)}", findings, seen);
            }
        }

        return findings;
    }

    private static IEnumerable<(object Key, object Value)> Resources(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var entry in Resources(merged))
            {
                yield return entry;
            }
        }

        foreach (var key in dictionary.Keys)
        {
            yield return (key, dictionary[key]);
        }
    }

    private static void Inspect(object? value, string where, List<string> findings, HashSet<object> seen)
    {
        if (value is null || !seen.Add(value))
        {
            return;
        }

        switch (value)
        {
            case Storyboard or AnimationTimeline:
                findings.Add($"{where} is an animation");
                break;
            case BeginStoryboard:
                findings.Add($"{where} starts a storyboard");
                break;
            case Style style:
                Inspect(style.BasedOn, where, findings, seen);
                foreach (var trigger in style.Triggers)
                {
                    Inspect(trigger, where, findings, seen);
                }

                foreach (var setter in style.Setters.OfType<Setter>())
                {
                    Inspect(setter.Value, where, findings, seen);
                }

                break;
            case FrameworkTemplate template:
                if (template is ControlTemplate control)
                {
                    foreach (var trigger in control.Triggers)
                    {
                        Inspect(trigger, where, findings, seen);
                    }
                }
                else if (template is DataTemplate data)
                {
                    foreach (var trigger in data.Triggers)
                    {
                        Inspect(trigger, where, findings, seen);
                    }
                }

                break;
            case TriggerBase trigger:
                foreach (var action in trigger.EnterActions.Concat(trigger.ExitActions))
                {
                    Inspect(action, where, findings, seen);
                }

                if (trigger is EventTrigger events)
                {
                    foreach (var action in events.Actions)
                    {
                        Inspect(action, where, findings, seen);
                    }
                }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// With the high-contrast palette applied, every opaque colour drawn is one of the system's (Spec §7:
    /// tokens map to system colours). A colour written into a view instead of a token would stay the same
    /// whatever palette the person chose, which is the failure high contrast exists to prevent.
    /// </summary>
    public static List<string> OffPalette(Window window)
    {
        var palette = typeof(SystemColors).GetProperties()
            .Where(p => p.PropertyType == typeof(Color))
            .Select(p => (Color)p.GetValue(null)!)
            .ToHashSet();
        var findings = new List<string>();
        foreach (var element in Descendants(window).OfType<FrameworkElement>().Where(e => e.IsVisible))
        {
            foreach (var (what, brush) in Brushes(element))
            {
                if (brush is SolidColorBrush { Color.A: 255 } solid && !palette.Contains(solid.Color))
                {
                    findings.Add($"{Describe(element)} draws its {what} in #{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}, not a system colour, under high contrast");
                }
            }
        }

        return findings;
    }

    private static IEnumerable<(string What, Brush? Brush)> Brushes(FrameworkElement element)
    {
        switch (element)
        {
            case Border border:
                yield return ("background", border.Background);
                if (border.BorderThickness != default)
                {
                    yield return ("border", border.BorderBrush);
                }

                break;
            case Panel panel:
                yield return ("background", panel.Background);
                break;
            case TextBlock text:
                yield return ("text", text.Foreground);
                yield return ("background", text.Background);
                break;
            case Shape shape:
                yield return ("fill", shape.Fill);
                yield return ("outline", shape.Stroke);
                break;
            // Text is drawn by text blocks, checked above, and by the edit controls' own text view.
            case TextBoxBase or PasswordBox:
                yield return ("text", ((Control)element).Foreground);
                break;
            default:
                break;
        }
    }
}
