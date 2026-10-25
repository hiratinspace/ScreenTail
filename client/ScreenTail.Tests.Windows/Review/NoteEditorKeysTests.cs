using System.Runtime.Versioning;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The note pane's chords (ST-074, Spec §5 S3; 2026-10-12 review T-168, T-240). A multiline TextBox runs
/// Enter and Backspace as editing commands and marks them handled before a KeyDown handler on it sees
/// them, so Enter never started a step; and under Alt WPF reports the key as <see cref="Key.System"/>
/// with the letter in <c>SystemKey</c>, so Alt+C and Alt+arrows never fired. The routing is one pure
/// function, and the view listens on PreviewKeyDown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NoteEditorKeysTests
{
    [Fact]
    public void AltCIsConfirmEvenThoughWpfReportsItAsSystem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        Assert.Equal(NoteEditorKey.ConfirmStep, NoteEditorKeys.Route(Key.System, Key.C, ModifierKeys.Alt, caretAtEnd: false, empty: false));
        Assert.Equal(NoteEditorKey.MoveUp, NoteEditorKeys.Route(Key.System, Key.Up, ModifierKeys.Alt, caretAtEnd: false, empty: false));
        Assert.Equal(NoteEditorKey.MoveDown, NoteEditorKeys.Route(Key.System, Key.Down, ModifierKeys.Alt, caretAtEnd: true, empty: false));

        // Plain letters type; Alt+Enter is not a new step; Enter mid-text is a line break the box keeps.
        Assert.Equal(NoteEditorKey.None, NoteEditorKeys.Route(Key.C, Key.None, ModifierKeys.None, caretAtEnd: true, empty: false));
        Assert.Equal(NoteEditorKey.None, NoteEditorKeys.Route(Key.System, Key.Enter, ModifierKeys.Alt, caretAtEnd: true, empty: false));
        Assert.Equal(NoteEditorKey.None, NoteEditorKeys.Route(Key.Enter, Key.None, ModifierKeys.None, caretAtEnd: false, empty: false));
        Assert.Equal(NoteEditorKey.AddStep, NoteEditorKeys.Route(Key.Enter, Key.None, ModifierKeys.None, caretAtEnd: true, empty: false));
        Assert.Equal(NoteEditorKey.RemoveStep, NoteEditorKeys.Route(Key.Back, Key.None, ModifierKeys.None, caretAtEnd: true, empty: true));
        Assert.Equal(NoteEditorKey.None, NoteEditorKeys.Route(Key.Back, Key.None, ModifierKeys.None, caretAtEnd: false, empty: false));
    }

    [Fact]
    public void EnterAtTheEndOfAStepStartsTheNextOneAndBackspaceRemovesAnEmptyOne()
    {
        // T-168, against a real multiline TextBox: the editing commands take Enter and Backspace on
        // KeyDown, so the step box has to act on PreviewKeyDown or never act at all. Which event the view
        // listens on is pinned by TheStepBoxListensBeforeTheTextBoxDoes; this pins what the routing does.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = new NoteEditorViewModel(Session(Frames(1)));
            var current = model.Steps.Single();
            var box = new TextBox { AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap, Text = current.Text };
            box.PreviewKeyDown += (_, e) =>
            {
                switch (NoteEditorKeys.Route(e.Key, e.SystemKey, e.KeyboardDevice.Modifiers, box.CaretIndex == box.Text.Length, box.Text.Length == 0))
                {
                    case NoteEditorKey.AddStep:
                        current = model.AddStepAfter(current);
                        box.Text = current.Text;
                        e.Handled = true;
                        break;
                    case NoteEditorKey.RemoveStep:
                        var previous = model.Steps[model.Steps.IndexOf(current) - 1];
                        if (model.RemoveStep(current))
                        {
                            current = previous;
                            box.Text = current.Text;
                        }

                        e.Handled = true;
                        break;
                    default:
                        break;
                }
            };
            box.CaretIndex = box.Text.Length;

            Assert.True(UiThread.Press(box, Key.Enter));

            Assert.Equal(2, model.Steps.Count);
            Assert.Equal("Checked the spooler service.", model.Steps[0].Text);
            Assert.Equal(string.Empty, model.Steps[1].Text);
            Assert.Equal(string.Empty, box.Text);

            Assert.True(UiThread.Press(box, Key.Back));

            Assert.Equal("Checked the spooler service.", Assert.Single(model.Steps).Text);
            Assert.Equal("Checked the spooler service.", box.Text);
            await System.Windows.Threading.Dispatcher.Yield();
        });
    }

    [Fact]
    public void TheStepBoxListensBeforeTheTextBoxDoes()
    {
        // The wiring itself, as NoteEditorView.xaml declares it. The XAML is copied beside the tests at build.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Review", "Xaml", "NoteEditorView.xaml"));
        var box = Assert.Single(xaml.Descendants(), e => e.Name.LocalName == "TextBox" && (string?)e.Attribute(XName.Get("AutomationProperties.Name")) == "Step text");

        Assert.Equal("OnStepKeyDown", (string?)box.Attribute("PreviewKeyDown"));
        Assert.Null(box.Attribute("KeyDown"));
    }

    [Fact]
    public void AFrameChipIsBoundToTheNotesShowFrameCommand()
    {
        // T-165, the XAML half: the chip reaches the strip through ShowFrameCommand on the pane's own
        // view model, which ReviewPaneTests.ClickingAFrameChipSelectsThatFrameInTheStrip follows the rest of the way.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Review", "Xaml", "NoteEditorView.xaml"));
        var chip = Assert.Single(xaml.Descendants(), e => e.Name.LocalName == "Button" && (string?)e.Attribute("Tag") == "{Binding Id}");

        Assert.Equal("{Binding DataContext.ShowFrameCommand, RelativeSource={RelativeSource AncestorType=UserControl}}", (string?)chip.Attribute("Command"));
        Assert.Equal("{Binding Id}", (string?)chip.Attribute("CommandParameter"));
    }
}
