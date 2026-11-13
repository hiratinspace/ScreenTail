using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.Tests.Windows.Accessibility;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The app's one dialog (Spec §3, v0.6.11) opens with the app's own styles, only lets the technician act
/// with the exact phrase when one is asked for, and names its buttons by what they do. From #86 to #228
/// the tray's dialog carried a TextBlock style on its box, which WPF refuses with
/// InvalidOperationException as the window is built, so the dialog could never have opened; no test built
/// it then. ScreenTail.Tests' StyleTargetTypeTests holds the same rule for every view from the source.
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
            // The words LiveShell passes for discard_session, with the phrase the service gives.
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(ConfirmCopy.DiscardSession, "DISCARD"));
            try
            {
                Assert.True(dialog.IsLoaded, "The discard confirmation did not load.");

                var typed = Audit.Descendants(dialog).OfType<TextBox>().Single();
                Assert.Equal(typeof(TextBox), typed.Style?.TargetType);
                Assert.Contains("Mono", typed.FontFamily.Source, StringComparison.Ordinal);

                // The button says what happens, in the danger style.
                var confirm = Screens.Named<Button>(dialog, "Discard session");
                Assert.Equal("Discard session", confirm.Content);
                Assert.Same(dialog.FindResource("Button.Danger"), confirm.Style);
                Assert.False(confirm.IsEnabled, "Discard is enabled before anything was typed.");

                typed.Text = "discard";
                await Audit.Settle();
                Assert.False(confirm.IsEnabled, "Discard accepted the phrase in the wrong case.");

                typed.Text = "DISCARD";
                await Audit.Settle();
                Assert.True(confirm.IsEnabled, "Discard stayed disabled after the exact phrase was typed.");
                Assert.True(confirm.IsDefault, "Enter does not discard once the phrase is typed.");

                var keep = Screens.Named<Button>(dialog, "Keep it");
                Assert.True(keep.IsCancel, "Esc does not press Keep it.");
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }

    [Fact]
    public async Task APlainQuestionHasNoBoxAndItsFilledButtonIsTheOrdinaryOne()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(ConfirmCopy.MarkAsPublished("ScreenConnect session, started Mon 14 Sep 16:40, 9 min")));
            try
            {
                Assert.DoesNotContain(Audit.Descendants(dialog).OfType<TextBox>(), b => b.IsVisible);
                var mark = Screens.Named<Button>(dialog, "Mark as published");
                Assert.Same(dialog.FindResource("Button.Primary"), mark.Style);
                Assert.True(mark.IsEnabled);
                Assert.False(mark.IsDefault, "Enter vouches for the ticket by reflex.");
                Assert.True(Screens.Named<Button>(dialog, "Cancel").IsDefault);
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }

    [Fact]
    public async Task ANoticeHasOneButton()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(ConfirmCopy.NotDiscarded(ConfirmedAction.NotConnected)));
            try
            {
                var visible = Audit.Descendants(dialog).OfType<Button>().Where(b => b.IsVisible).ToList();
                var close = Assert.Single(visible);
                Assert.Equal("Close", close.Content);
                Assert.True(close.IsDefault && close.IsCancel, "Enter and Esc should both close a notice.");
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }

    [Fact]
    public async Task BulkDiscardNamesTheCountOnItsButton()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(ConfirmCopy.DiscardSessions(2), TypedConfirmation.ForCount(2)));
            try
            {
                var discard = Screens.Named<Button>(dialog, "Discard 2 sessions");
                Assert.False(discard.IsEnabled);
                Audit.Descendants(dialog).OfType<TextBox>().Single().Text = "2";
                await Audit.Settle();
                Assert.True(discard.IsEnabled);
                Assert.Equal(Visibility.Visible, Screens.Named<Button>(dialog, "Keep them").Visibility);
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }
}
