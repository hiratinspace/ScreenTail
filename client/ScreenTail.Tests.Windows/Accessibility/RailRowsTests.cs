using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenTail.Core.Shell;
using ScreenTail.UI.History;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Spec v0.6.9 §5 S4 on the real window at the shell's 1024 x 680 minimum, through WPF's own keyboard
/// pipeline (<see cref="Keys"/>): the rail is the narrow width there and its rows end long words in an
/// ellipsis inside it, <c>Enter</c> on a rail row opens its session and puts keyboard focus in the pane,
/// and <c>Enter</c> on an All sessions row opens that session as Open and a double-click do.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class RailRowsTests(TheApplication app)
{
    [Fact]
    public async Task AtTheSmallestWindowTheRailIsNarrowAndNoRowRunsPastIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = (ShellWindow)await Screens.OpenAsync("rail-not-confirmed");
            try
            {
                var rail = shell.RailFrame;
                Assert.Equal((double)shell.FindResource("Layout.rail-width-narrow"), rail.ActualWidth, 0.5);

                var edge = rail.PointToScreen(new Point(rail.ActualWidth, 0)).X;
                foreach (var row in Audit.Descendants(rail).OfType<ListBoxItem>().Where(r => r.IsVisible))
                {
                    foreach (var words in Audit.Descendants(row).OfType<TextBlock>().Where(t => t.IsVisible && t.ActualWidth > 0))
                    {
                        var right = words.PointToScreen(new Point(words.ActualWidth, 0)).X;
                        Assert.True(right <= edge + 0.5, $"\"{words.Text}\" ends {right - edge} px past the rail.");
                    }
                }

                // The labelled rows say the company and the note's title; a bare row says its tool.
                var model = Assert.IsType<InboxRailViewModel>(shell.Rail.DataContext);
                Assert.Contains(model.Rows, r => r.Title == "Northwind Traders" && r.Subtitle == "VPN drops every hour");
                Assert.Contains(model.Rows, r => r.Title == "Remote Desktop session" && r.Subtitle.Length == 0);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task EnterOnARailRowOpensItsSessionAndPutsFocusInThePane()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = (ShellWindow)await Screens.OpenAsync("rail-not-confirmed");
            try
            {
                var model = Assert.IsType<InboxRailViewModel>(shell.Rail.DataContext);
                var target = model.Rows.First(r => !r.IsOpen);
                var row = Audit.Descendants(shell.Rail).OfType<ListBoxItem>().Single(r => ReferenceEquals(r.DataContext, target));

                // Ctrl+arrows move focus in a list without selecting, so the row is reached unopened.
                _ = row.Focus();
                await Audit.Settle();
                Assert.False(target.IsOpen);

                _ = Keys.Press(shell, Key.Enter);
                await Screens.UntilAsync(() => shell.Sample!.Snapshot.Reviewing == target.Id && shell.Pane.IsKeyboardFocusWithin);

                Assert.Equal(ShellView.Review, shell.Sample!.Snapshot.View);
                Assert.Equal(target.Id, shell.Sample.Snapshot.Reviewing);
                Assert.True(shell.Pane.IsKeyboardFocusWithin, "Enter left keyboard focus outside the pane.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task EnterOnAnAllSessionsRowOpensThatSession()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = (ShellWindow)await Screens.OpenAsync("history");
            try
            {
                var table = Audit.Descendants(shell).OfType<DataGrid>().Single();
                await Screens.UntilAsync(() => table.Items.Count > 1 && table.ItemContainerGenerator.ContainerFromIndex(1) is DataGridRow);
                await Audit.Settle();
                var second = (DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(1);
                var session = Assert.IsType<HistoryRow>(second.Item);

                _ = Audit.Descendants(second).OfType<DataGridCell>().First().Focus();
                await Audit.Settle();
                Assert.True(Keys.Press(shell, Key.Enter), "Enter on a row was not handled.");
                await Screens.UntilAsync(() => shell.Sample!.Snapshot.View == ShellView.Review);

                Assert.Equal(session.Id, shell.Sample!.Snapshot.Reviewing);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }
}
