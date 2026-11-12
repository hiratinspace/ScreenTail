using System.Runtime.Versioning;
using System.Windows.Controls;
using ScreenTail.Tests.Windows.Accessibility;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The tray's "Discard session" confirmation (LiveShell, Spec §3) opens, with the app's own styles, and
/// only lets the technician confirm with the exact phrase. From #86 to #228 its box carried a TextBlock
/// style, which WPF refuses with InvalidOperationException as the window is built, so the dialog could
/// never have opened; no test built it then. ScreenTail.Tests' StyleTargetTypeTests holds the same rule
/// for every view from the source.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class ConfirmWindowTests(TheApplication app)
{
    [Fact]
    public async Task TheDiscardConfirmationOpensAndAsksForTheExactPhrase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            // The words LiveShell passes for discard_session.
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(
                "Discard this session?",
                "Everything captured in it goes: the screenshots, what was said, and the note. This cannot be undone.",
                "DISCARD"));
            try
            {
                Assert.True(dialog.IsLoaded, "The discard confirmation did not load.");

                var typed = Audit.Descendants(dialog).OfType<TextBox>().Single();
                Assert.Equal(typeof(TextBox), typed.Style?.TargetType);
                Assert.Contains("Mono", typed.FontFamily.Source, StringComparison.Ordinal);

                var confirm = Screens.Named<Button>(dialog, "Discard this session?");
                Assert.False(confirm.IsEnabled, "Confirm is enabled before anything was typed.");

                typed.Text = "discard";
                await Audit.Settle();
                Assert.False(confirm.IsEnabled, "Confirm accepted the phrase in the wrong case.");

                typed.Text = "DISCARD";
                await Audit.Settle();
                Assert.True(confirm.IsEnabled, "Confirm stayed disabled after the exact phrase was typed.");
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }
}
