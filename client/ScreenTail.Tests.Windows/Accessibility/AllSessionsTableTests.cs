using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenTail.UI.Controls;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The All sessions table on the real window at the shell's 1024 x 680 minimum (Spec §5 S4, as built
/// v0.6.6; §3 Table; §7), driven through WPF's own keyboard pipeline (<see cref="Keys"/>). What the XAML
/// says is held on the Mac (AllSessionsTableTests there); this holds what it comes to once laid out: the
/// columns fill the table and nothing scrolls sideways, the selected row's tint and mark are the row's
/// and reach the table's right edge, and keyboard focus is an outline round the whole row, shown when
/// the arrows move and on no other row.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class AllSessionsTableTests(TheApplication app)
{
    [Fact]
    public async Task TheColumnsFillTheTableAndNothingScrollsSidewaysAtTheSmallestWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("history");
            try
            {
                var table = await TableAsync(shell);
                var viewport = Audit.Descendants(table).OfType<ScrollContentPresenter>().First();
                var scroller = Audit.Descendants(table).OfType<ScrollViewer>().First();

                var columns = table.Columns.Sum(c => c.ActualWidth);
                Assert.True(scroller.ScrollableWidth < 0.5, $"The table scrolls sideways by {scroller.ScrollableWidth} px at the smallest window.");
                Assert.True(
                    Math.Abs(columns - viewport.ActualWidth) <= 1,
                    $"The columns are {columns} px wide in a table {viewport.ActualWidth} px wide.");

                // Every row is as wide as the table, whatever its cells add up to.
                foreach (var row in Rows(table))
                {
                    Assert.True(row.ActualWidth >= viewport.ActualWidth - 0.5, $"A row is {row.ActualWidth} px wide in a table {viewport.ActualWidth} px wide.");
                }
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task TheArrowsSelectARowAndDrawItsTintMarkAndFocusOutlineAcrossTheWholeRow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("history");
            try
            {
                var table = await TableAsync(shell);
                var rows = Rows(table);
                var first = rows[0];
                var second = rows[1];

                _ = Audit.Descendants(first).OfType<DataGridCell>().First().Focus();
                await Audit.Settle();
                _ = Keys.Press(shell, Key.Down);
                await Audit.Settle();
                shell.UpdateLayout();

                Assert.True(second.IsSelected, "Down did not select the second row.");
                Assert.True(second.IsKeyboardFocusWithin, "Down did not move focus into the second row.");
                Assert.False(first.IsSelected, "Down left the first row selected.");

                // The tint is the row's own fill, in the token's colour, and no cell paints over it.
                Assert.Equal(Colour(second.FindResource("Brush.bg.selected")), Colour(second.Background));
                foreach (var cell in Audit.Descendants(second).OfType<DataGridCell>())
                {
                    Assert.True(cell.Background is null || Colour(cell.Background).A == 0, "A selected row's cell paints a fill of its own.");
                }

                // The mark at the leading edge: on the selected row and no other.
                Assert.True(Part(second, "Indicator").IsVisible, "The selected row has no accent mark.");
                Assert.False(Part(first, "Indicator").IsVisible, "A row that is not selected shows the accent mark.");

                // Keyboard focus: the outline round the row the arrows moved to, as wide as the row.
                var outline = Part(second, "FocusOutline");
                Assert.True(RowFocus.GetShown(second), "The row focus arrived in by keyboard does not say so.");
                Assert.True(outline.IsVisible, "The focused row draws no focus outline.");
                Assert.True(Math.Abs(outline.ActualWidth - second.ActualWidth) < 0.5, $"The outline is {outline.ActualWidth} px wide on a row {second.ActualWidth} px wide.");
                Assert.True(Math.Abs(outline.ActualHeight - second.ActualHeight) < 0.5, $"The outline is {outline.ActualHeight} px tall on a row {second.ActualHeight} px tall.");
                Assert.Equal(Colour(second.FindResource("Brush.accent.primary")), Colour(outline.Stroke));
                Assert.Equal(2, outline.StrokeThickness);

                // And on no row focus has left, nor round a single cell.
                Assert.False(RowFocus.GetShown(first), "The row focus left still says it has keyboard focus.");
                Assert.False(Part(first, "FocusOutline").IsVisible, "The row focus left still draws the outline.");
                Assert.All(Audit.Descendants(table).OfType<DataGridCell>(), cell => Assert.Null(cell.FocusVisualStyle));

                // Left and Right move the cell inside the row; the outline stays the row's.
                _ = Keys.Press(shell, Key.Right);
                await Audit.Settle();
                Assert.True(second.IsKeyboardFocusWithin, "Right took focus out of the row.");
                Assert.True(Part(second, "FocusOutline").IsVisible, "Right took the outline off the row.");

                // Focus that leaves the table takes the outline with it; the selection stays.
                _ = Screens.Named<Button>(shell, "Open").Focus();
                await Audit.Settle();
                Assert.False(RowFocus.GetShown(second), "The outline stayed on a row focus had left.");
                Assert.True(second.IsSelected, "Leaving the table cleared the selection.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    private static async Task<DataGrid> TableAsync(Window shell)
    {
        var table = Audit.Descendants(shell).OfType<DataGrid>().Single();
        await Screens.UntilAsync(() => table.Items.Count > 1 && table.ItemContainerGenerator.ContainerFromIndex(1) is DataGridRow);
        for (var i = 0; i < 5; i++)
        {
            await Audit.Settle();
        }

        shell.UpdateLayout();
        Assert.True(table.Items.Count > 1, "The sample has fewer than two sessions.");
        return table;
    }

    private static List<DataGridRow> Rows(DataGrid table) =>
        [.. Enumerable.Range(0, table.Items.Count).Select(i => table.ItemContainerGenerator.ContainerFromIndex(i)).OfType<DataGridRow>()];

    private static Rectangle Part(DataGridRow row, string name) =>
        Audit.Descendants(row).OfType<Rectangle>().Single(r => r.Name == name);

    private static Color Colour(object? brush) =>
        brush is SolidColorBrush solid ? solid.Color : throw new InvalidOperationException($"Not a solid brush: {brush}.");
}
