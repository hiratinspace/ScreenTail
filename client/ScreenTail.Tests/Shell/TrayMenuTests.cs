using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-016, ST-071: the tray's quick menu to Spec §5 S1 (as built v0.6.10). The host draws these rows and
/// nothing else, so order, words, what is disabled and INV-4's rule for the pill are held here.
/// </summary>
public sealed class TrayMenuTests
{
    [Fact]
    public void WhileRecordingTheMenuRunsInTheSpecsOrder()
    {
        var rows = Rows(Capture(CaptureStates.Recording));

        Assert.Equal(
            [
                "Recording: ScreenConnect (12:41)",
                "Pause", "Stop and draft", "Discard session…",
                "-",
                "Open ScreenTail", "Session history", "What's being captured right now?", "Hide the recording pill (only between sessions)",
                "-",
                "Settings", "Set up ScreenTail…", "Quit ScreenTail",
            ],
            rows.Select(r => r.Kind == TrayRowKind.Separator ? "-" : r.Name));
        Assert.Equal(TrayRowKind.State, rows[0].Kind);
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public void TheCaptureControlsNameTheirHotkeys()
    {
        var rows = Rows(Capture(CaptureStates.Recording));

        Assert.Equal("Ctrl+Alt+P", Row(rows, TrayCommand.Pause).Hotkey);
        Assert.Equal("Ctrl+Alt+S", Row(rows, TrayCommand.Stop).Hotkey);
        Assert.Null(Row(rows, TrayCommand.Discard).Hotkey);
    }

    [Fact]
    public void PausedOffersResumeOnTheSameRow()
    {
        // The tray sent pause whatever the state, so a paused session could not be resumed from it.
        var rows = Rows(Capture(CaptureStates.Paused));

        Assert.Equal(TrayCommand.Resume, rows[1].Command);
        Assert.Equal("Resume", rows[1].Name);
        Assert.Equal("Ctrl+Alt+P", rows[1].Hotkey);
        Assert.True(rows[1].Enabled);
        Assert.DoesNotContain(rows, r => r.Command == TrayCommand.Pause);
    }

    [Theory]
    [InlineData(CaptureStates.Recording, true, true)]
    [InlineData(CaptureStates.Suppressed, true, true)]
    [InlineData(CaptureStates.Finalizing, false, false)]
    [InlineData("new_state", false, false)]
    public void EachControlIsEnabledOnlyWhereTheServiceAcceptsIt(string state, bool pause, bool stop)
    {
        var rows = Rows(Capture(state));

        Assert.Equal(pause, Row(rows, TrayCommand.Pause).Enabled);
        Assert.Equal(stop, Row(rows, TrayCommand.Stop).Enabled);
        Assert.True(Row(rows, TrayCommand.Discard).Enabled);
    }

    [Theory]
    [InlineData(CaptureStates.Idle, false)]
    [InlineData(CaptureStates.DraftPending, true)]
    [InlineData(CaptureStates.DraftReady, true)]
    [InlineData(CaptureStates.DraftFailed, true)]
    public void BetweenSessionsTheFirstBlockSaysHowToStart(string state, bool discard)
    {
        var rows = Rows(Capture(state));

        Assert.Equal(TrayRowKind.Hint, rows[1].Kind);
        Assert.Equal("Start capture: press Ctrl+Alt+R in the remote session", rows[1].Name);
        Assert.False(rows[1].Enabled);
        Assert.DoesNotContain(rows, r => r.Command is TrayCommand.Pause or TrayCommand.Resume or TrayCommand.Stop);
        Assert.Equal(discard, rows.Any(r => r.Command == TrayCommand.Discard));
    }

    [Theory]
    [InlineData(ServiceConnection.Connecting)]
    [InlineData(ServiceConnection.Unavailable)]
    public void WithNoServiceNothingThatSendsIsEnabled(ServiceConnection connection)
    {
        // The UI does not know whether a session is under way, so the controls stay where they are and
        // cannot be chosen: a command into a dropped pipe would look done and not be.
        var rows = TrayMenu.For(new ShellSnapshot(connection, Capture(CaptureStates.Recording), ShellView.Review, null), pillHidden: false);

        Assert.False(Row(rows, TrayCommand.Pause).Enabled);
        Assert.False(Row(rows, TrayCommand.Stop).Enabled);
        Assert.False(Row(rows, TrayCommand.Discard).Enabled);
        Assert.False(Row(rows, TrayCommand.HidePill).Enabled);
        Assert.True(Row(rows, TrayCommand.Quit).Enabled);
        Assert.True(Row(rows, TrayCommand.Diagnostics).Enabled);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    [InlineData("new_state")]
    public void ThePillCannotBePutAwayWhileASessionMayBeUnderWay(string state)
    {
        // INV-4, owner decision 2026-10-25: tray-only between sessions only. The item used to be offered
        // in every state, and during a recording it hid the pill the moment the session ended instead.
        foreach (var hidden in new[] { false, true })
        {
            var row = Row(Rows(Capture(state), hidden), TrayCommand.HidePill);
            Assert.False(row.Enabled);
            Assert.Equal("Hide the recording pill (only between sessions)", row.Name);
        }
    }

    [Fact]
    public void BetweenSessionsThePillCanBePutAwayAndBroughtBack()
    {
        var shown = Row(Rows(Capture(CaptureStates.Idle)), TrayCommand.HidePill);
        Assert.True(shown.Enabled);
        Assert.Equal("Hide the recording pill until the next session", shown.Name);

        var hidden = Rows(Capture(CaptureStates.Idle), pillHidden: true);
        Assert.DoesNotContain(hidden, r => r.Command == TrayCommand.HidePill);
        Assert.True(Row(hidden, TrayCommand.ShowPill).Enabled);
        Assert.Equal("Show the recording pill", Row(hidden, TrayCommand.ShowPill).Name);
    }

    [Theory]
    [InlineData(0, "Open ScreenTail")]
    [InlineData(1, "Open last draft (1 ready)")]
    [InlineData(2, "Open last draft (2 ready)")]
    public void OpenLastDraftCountsTheDraftsReady(int ready, string name)
    {
        var rows = Rows(Capture(CaptureStates.Idle) with { DraftsReady = ready });

        Assert.Equal(name, Row(rows, TrayCommand.OpenLastDraft).Name);
    }

    [Fact]
    public void EveryRowThatCanBeChosenHasItsOwnAccessKey()
    {
        // Win+B, the menu key, then a letter: the keyboard path through the menu without the arrows.
        foreach (var state in new[] { CaptureStates.Recording, CaptureStates.Paused, CaptureStates.Idle, CaptureStates.DraftReady })
        {
            foreach (var hidden in new[] { false, true })
            {
                var items = Rows(Capture(state) with { DraftsReady = 2 }, hidden).Where(r => r.Kind == TrayRowKind.Item).ToList();
                Assert.All(items, r => Assert.NotNull(r.AccessKey));
                Assert.Equal(items.Count, items.Select(r => r.AccessKey).Distinct().Count());
            }
        }

        Assert.All(Rows(Capture(CaptureStates.Idle)).Where(r => r.Kind is TrayRowKind.State or TrayRowKind.Hint), r => Assert.Null(r.AccessKey));
    }

    [Fact]
    public void NoRowCarriesAnEmDashOrAnExclamationMark()
    {
        foreach (var state in new[] { CaptureStates.Recording, CaptureStates.Paused, CaptureStates.Idle, CaptureStates.DraftReady, CaptureStates.Finalizing })
        {
            Assert.All(Rows(Capture(state)), r =>
            {
                Assert.DoesNotContain("—", r.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("!", r.Text, StringComparison.Ordinal);
            });
        }
    }

    [Theory]
    [InlineData(CaptureStates.Recording, true)]
    [InlineData(CaptureStates.Paused, true)]
    [InlineData(CaptureStates.Suppressed, true)]
    [InlineData(CaptureStates.Finalizing, false)]
    [InlineData(CaptureStates.DraftReady, false)]
    [InlineData(CaptureStates.Idle, false)]
    public void DiscardStopsCaptureBeforeItAsksWhileASessionIsBeingCaptured(string state, bool stopsFirst)
    {
        // Spec v0.4.1 Q3 and §4: no modal while a session is recording, so the session stops first.
        Assert.Equal(stopsFirst, TrayMenu.DiscardStopsFirst(Capture(state)));
    }

    [Theory]
    [InlineData(CaptureStates.Recording, "stop,ask")]
    [InlineData(CaptureStates.Paused, "stop,ask")]
    [InlineData(CaptureStates.DraftReady, "ask")]
    public async Task TheConfirmationIsAskedOnlyOnceTheStopIsAnswered(string state, string order)
    {
        var said = new List<string>();
        var stopped = new TaskCompletionSource();

        var discard = TrayMenu.DiscardAsync(
            Capture(state),
            async () =>
            {
                said.Add("stop");
                await stopped.Task;
            },
            () =>
            {
                said.Add("ask");
                return Task.CompletedTask;
            });

        // While the stop is unanswered, nothing is asked: the session may still be recording.
        if (order.StartsWith("stop", StringComparison.Ordinal))
        {
            Assert.Equal(["stop"], said);
        }
        stopped.SetResult();
        await discard;

        Assert.Equal(order, string.Join(',', said));
    }

    private static IReadOnlyList<TrayRow> Rows(CaptureStateSnapshot capture, bool pillHidden = false) =>
        TrayMenu.For(new ShellSnapshot(ServiceConnection.Connected, capture, ShellView.Review, null), pillHidden);

    private static TrayRow Row(IReadOnlyList<TrayRow> rows, TrayCommand command) => rows.Single(r => r.Command == command);

    private static CaptureStateSnapshot Capture(string state) => new()
    {
        State = state,
        SessionId = "s1",
        RemoteTool = "screenconnect",
        ElapsedMs = (12 * 60 * 1000) + 41_000,
    };
}
