using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ScreenTail.Core.Review.Publish;
using ScreenTail.UI.Review.Publish;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The envelope's As line at the shell's 1024 by 680 minimum (Spec §5 S3, v0.6.12): the choice "Discussion:
/// the customer sees it" was cut to "Discussion: the custo" by a half-width column. Nothing on the line is
/// cut off now; where As and Time do not fit side by side, Time moves under As.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class EnvelopeFitsTests(TheApplication app)
{
    [Fact]
    public async Task AtTheMinimumWindowTheAsChoiceIsNotCutOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                shell.Width = shell.MinWidth;
                shell.Height = shell.MinHeight;
                await Audit.Settle();
                shell.UpdateLayout();

                var envelope = Audit.Descendants(shell).OfType<EnvelopeView>().Single();
                var discussion = Audit.Descendants(envelope).OfType<RadioButton>().Single(r => AutomationProperties.GetName(r) == PublishPanel.DiscussionLabel);
                var holder = (FrameworkElement)Audit.Descendants(envelope).OfType<WrapPanel>().Single(w => w.Name == "AsAndTime").Children[0];

                // Every pixel of the choice is inside what holds it, and inside the envelope.
                var right = discussion.TranslatePoint(new Point(discussion.ActualWidth, 0), holder).X;
                Assert.True(right <= holder.ActualWidth + 0.5, $"The Discussion choice ends at {right:F0} in a box {holder.ActualWidth:F0} wide.");
                var inEnvelope = discussion.TranslatePoint(new Point(discussion.ActualWidth, 0), envelope).X;
                Assert.True(inEnvelope <= envelope.ActualWidth + 0.5, $"The Discussion choice ends at {inEnvelope:F0} in an envelope {envelope.ActualWidth:F0} wide.");

                // Its words are laid out whole.
                var words = Audit.Descendants(discussion).OfType<TextBlock>().Single(t => t.Text == PublishPanel.DiscussionLabel);
                Assert.True(words.ActualWidth + 0.5 >= words.DesiredSize.Width, "The Discussion choice's words are narrower than they need.");

                // Time does not sit over it.
                var minutes = Audit.Descendants(envelope).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Minutes");
                var minutesAt = minutes.TranslatePoint(default, envelope);
                var discussionAt = discussion.TranslatePoint(default, envelope);
                Assert.True(
                    minutesAt.Y >= discussionAt.Y + discussion.ActualHeight - 0.5 || minutesAt.X >= inEnvelope,
                    "The minutes box overlaps the Discussion choice.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }
}
