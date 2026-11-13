using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The shortcut sheet Spec §4 promises behind <c>?</c> (ST-084: it was in the spec and the audit, and
/// nothing opened it). Every chord Spec §5 names, grouped by where it works. The capture hotkeys are the
/// shipped ones; Settings, under Capture, is where a technician changes them, and the sheet says so.
///
/// Not modal. Spec §4 allows no modal while a session records, and a technician may well ask what the
/// keys are mid-session; an owned window that Esc closes needs no modal loop to behave like one.
/// </summary>
public partial class ShortcutSheet : Window
{
    public ShortcutSheet()
    {
        InitializeComponent();
    }

    /// <summary>One line of the sheet: the keys, and what they do.</summary>
    public sealed record Chord(string Keys, string Action);

    /// <summary>A heading and its chords.</summary>
    public sealed record Group(string Name, IReadOnlyList<Chord> Chords);

    /// <summary>What the sheet lists, in Spec §5's words.</summary>
    public static IReadOnlyList<Group> Groups { get; } =
    [
        new("Everywhere", [
            new("?", "This sheet, when the cursor is not in a text box"),
            new("Tab, Shift+Tab", "Move to the next or previous control"),
            new("Ctrl+F", "Search the sessions in the rail"),
            new("Esc", "Close a dialog, this sheet or an enlarged screenshot"),
        ]),
        new("During a session (change these in Settings, under Capture)", [
            new("Ctrl+Alt+R", "Start capture"),
            new("Ctrl+Alt+P", "Pause or resume"),
            new("Ctrl+Alt+S", "Stop and draft"),
            new("Ctrl+Alt+M", "Mark a moment"),
        ]),
        new("Review", [
            new("Ctrl+Enter", "Publish"),
            new("Ctrl+S", "Save the note now"),
            new("Alt+1, Alt+2, Alt+3", "Go to the note, the screenshots or the publish pane"),
            new("Alt+T", "Open or close the timeline and transcript"),
        ]),
        new("In a step of the note", [
            new("Alt+C", "Confirm a step marked as inferred"),
            new("Alt+Up, Alt+Down", "Move the step up or down"),
            new("Enter at the end", "Start a new step"),
            new("Backspace when empty", "Delete the step"),
            new("Space, Enter, Del", "On its screenshot: include or exclude, enlarge, delete"),
        ]),
        new("In the screenshots", [
            new("Space", "Include or exclude the screenshot"),
            new("Enter", "Enlarge it"),
            new("Del", "Delete it (undo for 5 seconds)"),
            new("Ctrl+Z", "Undo the delete"),
            new("T", "Show the text read off an enlarged screenshot"),
            new("B", "Blur a region of an enlarged screenshot: drag, or place the box"),
            new("Arrows", "Move the blur box"),
            new("Shift+arrows", "Resize the blur box"),
            new("Ctrl+arrows", "Bigger steps, with or without Shift"),
            new("Enter, Esc", "Blur what the box covers, or put it away"),
        ]),
        new("Settings", [
            new("Ctrl+S", "Save every change on the page"),
            new("Up, Down", "Move through the sections"),
            new("Esc on the Save bar", "Undo the changes"),
        ]),
    ];

    /// <summary>
    /// The sheet's left column: anywhere, during a session, in Review and inside a step. The step's group
    /// sits here so neither column grows the sheet past a 768 px screen.
    /// </summary>
    public static IReadOnlyList<Group> LeftColumn { get; } = [.. Groups.Take(4)];

    /// <summary>The right column: in the screenshots and in Settings.</summary>
    public static IReadOnlyList<Group> RightColumn { get; } = [.. Groups.Skip(4)];

    /// <summary>
    /// Whether <c>?</c> typed with focus on <paramref name="focused"/> means the sheet. In a text box, a
    /// password box or an editable combo box it is a question mark the technician is typing.
    /// </summary>
    public static bool Opens(string text, IInputElement? focused) =>
        text == "?" && focused is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox { IsEditable: true });

    /// <summary>Opens the sheet over <paramref name="owner"/>, or brings back the one already open.</summary>
    public static ShortcutSheet Open(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.OwnedWindows.OfType<ShortcutSheet>().FirstOrDefault() is { } open)
        {
            _ = open.Activate();
            return open;
        }

        var sheet = new ShortcutSheet { Owner = owner };
        sheet.Show();
        return sheet;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
