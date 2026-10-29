using System.Globalization;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Sessions;
using ScreenTail.Platform.Ipc;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Shared.Settings;

namespace ScreenTail.E2E;

/// <summary>
/// The loop, step by step, through the same pipe commands the UI sends (ST-013).
///
/// What it does not do is as important as what it does. It never sends <c>indicator_showing</c>: a session
/// may only start because the real pill told the service it is on screen (INV-4). It never reads the
/// store or a frame's pixels: what was stored is asked of the service, which filters to redacted frames
/// (INV-1), and what reached the backend is asked of the demo PSA's read endpoint. And nothing it prints
/// is a window title, an image or the test secret.
/// </summary>
internal sealed class Loop(Steps steps, Uri backend, string secret, bool injectClicks, TimeSpan draftTimeout) : IAsyncDisposable
{
    /// <summary>Where the runner puts the run's fake secret. Made up per run; never printed.</summary>
    public const string SecretVariable = "SCREENTAIL_E2E_SECRET";

    /// <summary>The demo ticket the story publishes to (docs/dev/demo-psa.md).</summary>
    public const string Ticket = "48213";

    /// <summary>The words the scripted provider writes into every note (backend ScriptedProvider.Label).</summary>
    public const string ScriptedLabel = "drafted by the scripted test provider";

    private const string ProcessName = "ScreenTail.E2E";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IpcClient? _pipe;
    private TestWindow? _window;
    private CaptureSettings? _original;
    private string? _session;
    private Session? _stored;
    private List<string> _published = [];
    private int _restored;

    /// <summary>What the window shows first: an ordinary checklist, the ticket, and a line redaction must mask.</summary>
    private string ScreenOne => string.Join(
        Environment.NewLine,
        "Front desk printer queue checklist",
        $"Ticket {Ticket} reception printer offline",
        $"pass: {secret}",
        "Queue cleared and a test page sent");

    /// <summary>What it shows second: the screen changing, as a technician's does.</summary>
    private static string ScreenTwo => string.Join(
        Environment.NewLine,
        "Front desk printer queue checklist",
        "Step two: the spooler restarted",
        "The reception printer is back online");

    private IpcClient Pipe => _pipe ?? throw new StepFailed("There is no pipe connection; an earlier step failed.");

    public async Task RunAsync()
    {
        try
        {
            _ = await RunStepsAsync().ConfigureAwait(false);
        }
        finally
        {
            await RestoreAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> RunStepsAsync() =>
        await steps.RunAsync("pipe", "the capture service's pipe to accept this driver (unsigned build: same-folder handshake)", ConnectAsync).ConfigureAwait(false)
        && await steps.RunAsync("ui", "the UI process to be running", UiRunningAsync).ConfigureAwait(false)
        && await steps.RunAsync("all-windows", "Settings > Capture to accept All windows over the pipe, as the Capture pane saves it", AllWindowsAsync).ConfigureAwait(false)
        && await steps.RunAsync("test-window", "the test window with known text to take the foreground", ShowWindowAsync).ConfigureAwait(false)
        && await steps.RunAsync("scope", "the service's scope decision to say the test window is captured", ScopeAsync).ConfigureAwait(false)
        && await steps.RunAsync("start", injectClicks ? "the start chord (Ctrl+Alt+R) to start a session, which needs the UI's indicator report" : "a session start over the pipe to be accepted, which needs the UI's indicator report", StartAsync).ConfigureAwait(false)
        && await steps.RunAsync("pill", "the recording pill on a monitor, and capture to stay unsuppressed while it reports", PillAsync).ConfigureAwait(false)
        && await steps.RunAsync("capture", injectClicks ? "real left clicks on the test window to be accepted" : "mark-moment captures of the test window (no input injection on this runner)", CaptureAsync).ConfigureAwait(false)
        && await steps.RunAsync("redaction", "the redaction worker to drain every frame it was handed", RedactionAsync).ConfigureAwait(false)
        && await steps.RunAsync("stop", injectClicks ? "the stop chord (Ctrl+Alt+S) to stop and draft" : "stop-and-draft over the pipe to be accepted", StopAsync).ConfigureAwait(false)
        && await steps.RunAsync("draft", "the draft to arrive (draft_ready for this session)", DraftAsync).ConfigureAwait(false)
        && await steps.RunAsync("stored-frames", "the stored frames to be redacted, with the pass line masked", StoredFramesAsync).ConfigureAwait(false)
        && await steps.RunAsync("frame-text", "the stored OCR text to carry the window's words and a mask, and never the secret", FrameTextAsync).ConfigureAwait(false)
        && await steps.RunAsync("note", "the stored note to be the scripted provider's, citing the frames", NoteAsync).ConfigureAwait(false)
        && await steps.RunAsync("integrations", "the backend to report the demo PSA as the tenant's PSA", IntegrationsAsync).ConfigureAwait(false)
        && await steps.RunAsync("ticket", $"the ticket search to find demo ticket #{Ticket}", TicketAsync).ConfigureAwait(false)
        && await steps.RunAsync("publish", $"publish_session to ticket #{Ticket} to succeed", PublishAsync).ConfigureAwait(false)
        && await steps.RunAsync("landed", $"the demo PSA's read endpoint to show the note on #{Ticket}, masked", LandedAsync).ConfigureAwait(false)
        && await steps.RunAsync("audit", "the audit export to hold this session's rows, its chain intact", AuditAsync).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _window?.Dispose();
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string> ConnectAsync()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var token = IpcTokenFile.Read(IpcTokenFile.DefaultPath);
                try
                {
                    _pipe = await IpcClient.ConnectAsync(
                        IpcPipeNames.ForUser(sid),
                        token,
                        clientName: ProcessName,
                        serverVerifier: new WindowsServerVerifier(Environment.ProcessPath!),
                        clientVersion: "e2e",
                        timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(token);
                }

                return $"Connected to service {_pipe.ServiceVersion}, which is {_pipe.State.State}.";
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or IpcProtocolException)
            {
                last = ex;
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        throw new StepFailed($"No connection in 60 s. Last: {last?.GetType().Name}: {last?.Message}");
    }

    private static Task<string> UiRunningAsync()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("ScreenTail.UI");
        try
        {
            return running.Length > 0
                ? Task.FromResult($"{running.Length} ScreenTail.UI process(es).")
                : throw new StepFailed("No ScreenTail.UI process is running.");
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    private async Task<string> AllWindowsAsync()
    {
        var reported = await Pipe.RequestAsync<CaptureSettingsReported>(id => new GetCaptureSettingsCommand { RequestId = id }).ConfigureAwait(false);
        _original = reported.Settings;
        var saved = await Pipe.SendAsync(id => new SetCaptureSettingsCommand
        {
            RequestId = id,
            Settings = reported.Settings with { CaptureAllWindows = true },
        }).ConfigureAwait(false);
        if (!saved.Ok)
        {
            throw new StepFailed($"set_capture_settings was refused: {saved.Error}");
        }

        var now = await Pipe.RequestAsync<CaptureSettingsReported>(id => new GetCaptureSettingsCommand { RequestId = id }).ConfigureAwait(false);
        return now.Settings.CaptureAllWindows
            ? $"All windows is on (it was {(reported.Settings.CaptureAllWindows ? "already on" : "off")}; restored at the end)."
            : throw new StepFailed("The save was accepted and the settings still say All windows is off.");
    }

    private Task<string> ShowWindowAsync()
    {
        _window = new TestWindow(ScreenOne);
        return Native.TakeForeground(_window.Handle, TimeSpan.FromSeconds(10))
            ? Task.FromResult("The test window is in front.")
            : throw new StepFailed($"The foreground stayed with {Native.Foreground()}.");
    }

    private async Task<string> ScopeAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var scope = "(none yet)";
        while (DateTime.UtcNow < deadline)
        {
            Front();
            var diagnostics = await Pipe.RequestAsync<DiagnosticsReported>(id => new GetDiagnosticsCommand { RequestId = id }).ConfigureAwait(false);
            scope = diagnostics.Scope;
            if (scope.StartsWith("Capturing", StringComparison.Ordinal) && scope.Contains(ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return $"Scope: {scope}";
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new StepFailed($"After 30 s the scope still says: {scope}. Foreground: {Native.Foreground()}.");
    }

    /// <summary>The chord the technician's capture settings bind to an action, or the shipped one.</summary>
    private string Hotkey(string action, string shipped) =>
        _original?.Hotkeys.TryGetValue(action, out var bound) == true && !string.IsNullOrWhiteSpace(bound) ? bound : shipped;

    private async Task<string> StartAsync()
    {
        if (injectClicks)
        {
            // The demo's way B: the chord, which starts through the coordinator rather than the pipe.
            var chord = Hotkey("StartCapture", "Ctrl+Alt+R");
            var pressed = 0;
            var started = Stopwatch();
            while (started.Elapsed < TimeSpan.FromSeconds(60))
            {
                Front();
                Native.Chord(chord);
                pressed++;
                if (await WaitForStateAsync(s => s.State == CaptureStates.Recording && s.SessionId is { Length: > 0 }, TimeSpan.FromSeconds(3)).ConfigureAwait(false) is { } recording)
                {
                    _session = recording.SessionId;
                    return $"Session {_session} is recording, started by {chord} (pressed {pressed} time(s)).";
                }
            }

            throw new StepFailed($"{chord} was pressed {pressed} time(s) in 60 s and no session started; the state is {Pipe.State.State}.");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(60);
        CommandResult result;
        var refusedForPill = 0;
        while (true)
        {
            Front();
            result = await Pipe.SendAsync(id => new StartCommand { RequestId = id }).ConfigureAwait(false);
            if (result.Ok)
            {
                break;
            }

            if (result.Error != ManualStart.NotIndicated)
            {
                throw new StepFailed($"The start was refused: {result.Error}");
            }

            refusedForPill++;
            if (clock.Elapsed > deadline)
            {
                throw new StepFailed($"For 60 s every start was refused because the service has no current indicator report from the UI ({refusedForPill} tries).");
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        var state = await WaitForStateAsync(s => s.State == CaptureStates.Recording && s.SessionId is { Length: > 0 }, TimeSpan.FromSeconds(10)).ConfigureAwait(false)
            ?? throw new StepFailed($"The start was accepted and the state is {Pipe.State.State}, not recording.");
        _session = state.SessionId;
        return $"Session {_session} is recording. The indicator was current after {refusedForPill} refusal(s).";
    }

    private async Task<string> PillAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Native.WindowRow? pill = null;
        while (pill is null && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            pill = Native.VisibleWindowsOf("ScreenTail.UI").FirstOrDefault(w => w.OnAMonitor && w.Width is >= 40 and <= 800 && w.Height is >= 16 and <= 200);
            if (pill is null)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        if (pill is null)
        {
            var seen = Native.VisibleWindowsOf("ScreenTail.UI").Select(w => $"{w.Class} {w.Width}x{w.Height}{(w.OnAMonitor ? "" : " off-screen")}");
            throw new StepFailed($"No pill-sized ScreenTail.UI window came up on a monitor in 10 s. Visible UI windows: {string.Join("; ", seen)}.");
        }

        // The indicator guard suppresses capture once reports stop: a pill that is up but not reporting
        // shows here as a suppressed session within the report lifetime and the guard's grace.
        var held = TimeSpan.FromSeconds(12);
        clock.Restart();
        while (clock.Elapsed < held)
        {
            Front();
            var state = Pipe.State;
            if (state.State != CaptureStates.Recording)
            {
                throw new StepFailed($"The session went {state.State} ({state.Reason ?? "no reason"}) {clock.Elapsed.TotalSeconds:0} s after the pill came up.");
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return $"Pill {pill.Class} {pill.Width}x{pill.Height} at {pill.Left},{pill.Top}{(pill.Topmost ? ", topmost" : "")}; recording held for {held.TotalSeconds:0} s.";
    }

    private async Task<string> CaptureAsync()
    {
        if (injectClicks)
        {
            for (var i = 0; i < 3; i++)
            {
                if (i == 2)
                {
                    _window!.Show(ScreenTwo);
                }

                Front();
                var (x, y) = Native.Middle(_window!.Handle);
                Native.Click(x, y);
                await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            }

            return "Three left clicks sent to the middle of the test window, the screen changing before the third.";
        }

        Front();
        var first = await Pipe.SendAsync(id => new MarkMomentCommand { RequestId = id }).ConfigureAwait(false);
        if (!first.Ok)
        {
            throw new StepFailed($"The first mark moment was refused: {first.Error}");
        }

        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _window!.Show(ScreenTwo);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Front();
        var second = await Pipe.SendAsync(id => new MarkMomentCommand { RequestId = id }).ConfigureAwait(false);
        if (!second.Ok)
        {
            throw new StepFailed($"The second mark moment was refused: {second.Error}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return "Two moments marked, the screen changing between them.";
    }

    private async Task<string> RedactionAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var quiet = 0;
        DiagnosticsReported? diagnostics = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(45))
        {
            diagnostics = await Pipe.RequestAsync<DiagnosticsReported>(id => new GetDiagnosticsCommand { RequestId = id }).ConfigureAwait(false);
            quiet = diagnostics.RedactionBacklog == 0 && Pipe.State.PendingRedactions == 0 ? quiet + 1 : 0;
            if (quiet >= 3)
            {
                return $"Backlog empty; {diagnostics.FramesDropped} frame(s) dropped unread, {diagnostics.EgressBlocked} request(s) refused by the egress guard.";
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new StepFailed($"After 45 s the backlog is {diagnostics?.RedactionBacklog} and the state counts {Pipe.State.PendingRedactions} pending.");
    }

    private static System.Diagnostics.Stopwatch Stopwatch() => System.Diagnostics.Stopwatch.StartNew();

    private async Task<string> StopAsync()
    {
        if (injectClicks)
        {
            var chord = Hotkey("StopAndDraft", "Ctrl+Alt+S");
            Front();
            Native.Chord(chord);
            return await WaitForStateAsync(s => s.State != CaptureStates.Recording, TimeSpan.FromSeconds(10)).ConfigureAwait(false) is { } after
                ? $"{chord} stopped the session; the state is {after.State}."
                : throw new StepFailed($"{chord} was pressed and the session is still recording.");
        }

        var stopped = await Pipe.SendAsync(id => new StopCommand { RequestId = id }).ConfigureAwait(false);
        return stopped.Ok
            ? $"Accepted; the state is {Pipe.State.State}."
            : throw new StepFailed($"The stop was refused: {stopped.Error}");
    }

    private async Task<string> DraftAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = await WaitForStateAsync(
            s => s.SessionId == _session && s.State is CaptureStates.DraftReady or CaptureStates.DraftFailed,
            draftTimeout).ConfigureAwait(false);
        if (state is null)
        {
            throw new StepFailed($"No draft in {draftTimeout.TotalSeconds:0} s; the state is {Pipe.State.State} for {Pipe.State.SessionId}.");
        }

        if (state.State == CaptureStates.DraftFailed)
        {
            throw new StepFailed($"The draft failed: {state.DraftFailureReason ?? "no reason given"}");
        }

        _ready = state;
        return $"Draft ready after {clock.Elapsed.TotalSeconds:0} s: {state.DraftTitle ?? "(no title in the state)"}.";
    }

    private CaptureStateSnapshot? _ready;

    private async Task<string> StoredFramesAsync()
    {
        var loaded = await Pipe.RequestAsync<SessionLoaded>(id => new GetSessionCommand { RequestId = id, SessionId = _session! }).ConfigureAwait(false);
        _stored = loaded.Session;
        var frames = _stored.Frames;
        if (frames.Count == 0)
        {
            throw new StepFailed($"The session has no stored frames; {_stored.FramesPurgedUnredacted} were purged unredacted.");
        }

        if (frames.FirstOrDefault(f => f.RedactionPending) is { } pending)
        {
            throw new StepFailed($"Frame {pending.Id} is handed out with redaction_pending true (INV-1).");
        }

        var kinds = frames.SelectMany(f => f.MaskedRegions).GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        if (!kinds.ContainsKey(MaskKind.Password))
        {
            throw new StepFailed($"No frame has a password mask over the pass line. Masks: {Describe(kinds)}; triggers: {string.Join(", ", frames.Select(f => f.Trigger))}.");
        }

        return $"{frames.Count} frame(s) ({string.Join(", ", frames.Select(f => f.Trigger))}), none pending, masks {Describe(kinds)}, {_stored.FramesPurgedUnredacted} purged unredacted.";
    }

    private async Task<string> FrameTextAsync()
    {
        var texts = new List<string>();
        foreach (var frame in _stored!.Frames)
        {
            var text = await Pipe.RequestAsync<FrameTextLoaded>(id => new GetFrameTextCommand { RequestId = id, FrameId = frame.Id }).ConfigureAwait(false);
            texts.Add(text.OcrText ?? string.Empty);
        }

        var all = string.Join("\n", texts);
        if (Leaks(all) is { } leak)
        {
            throw new StepFailed($"The stored OCR text carries the test secret ({leak}).");
        }

        if (!all.Contains("checklist", StringComparison.OrdinalIgnoreCase) || !all.Contains(Ticket, StringComparison.Ordinal))
        {
            throw new StepFailed($"The stored OCR text does not carry the window's words ({all.Length} characters over {texts.Count} frame(s)).");
        }

        return all.Contains("[REDACTED]", StringComparison.Ordinal)
            ? $"{texts.Count(t => t.Length > 0)} of {texts.Count} frame(s) have text; the window's words are there and the pass line reads [REDACTED]."
            : throw new StepFailed("The window's words are there and no [REDACTED] marker is: the pass line was read and not masked, or not read at all.");
    }

    private Task<string> NoteAsync()
    {
        var draft = _stored!.Draft ?? throw new StepFailed("The session is draft_ready and has no draft stored.");
        var body = string.Join("\n", [draft.Problem, .. draft.Steps.Select(s => s.Text), draft.Result]);
        if (!draft.Problem.Contains(ScriptedLabel, StringComparison.Ordinal))
        {
            throw new StepFailed("The note is not labelled as the scripted provider's.");
        }

        if (Leaks(body) is { } leak)
        {
            throw new StepFailed($"The note carries the test secret ({leak}).");
        }

        var ids = _stored.Frames.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var cited = draft.Steps.SelectMany(s => s.FrameRefs).ToList();
        if (cited.Count == 0 || cited.Any(id => !ids.Contains(id)))
        {
            throw new StepFailed($"The steps cite {cited.Count} frame(s), not all of them stored.");
        }

        // Spec section 6: the "Draft ready" notification names the draft, from the state the service sends.
        if (_ready?.DraftTitle != draft.SuggestedTitle)
        {
            throw new StepFailed($"draft_ready carried the title '{_ready?.DraftTitle}' and the note's is '{draft.SuggestedTitle}', so the notification cannot name it.");
        }

        return Task.FromResult($"{draft.Steps.Count} step(s) citing {cited.Count} frame(s), source {draft.Source}, prompt {draft.PromptVersion}, title in draft_ready.");
    }

    private async Task<string> IntegrationsAsync()
    {
        var listed = await Pipe.RequestAsync<IntegrationsListed>(id => new GetIntegrationsCommand { RequestId = id }).ConfigureAwait(false);
        var providers = listed.Integrations.Select(i => i.Provider).ToList();
        return providers.Contains("demo-psa", StringComparer.Ordinal)
            ? $"Integrations: {string.Join(", ", providers)}."
            : throw new StepFailed($"The demo PSA is not listed. Integrations: {string.Join(", ", providers)}. Is Providers:Demo on?");
    }

    private TicketRow? _ticket;

    private async Task<string> TicketAsync()
    {
        var found = await Pipe.RequestAsync<TicketsFound>(id => new SearchTicketsCommand { RequestId = id, Query = Ticket }).ConfigureAwait(false);
        _ticket = found.Tickets.FirstOrDefault(t => t.Id == Ticket)
            ?? throw new StepFailed($"The search answered {found.Tickets.Count} ticket(s), none #{Ticket}.");
        return $"#{_ticket.Id} for {_ticket.Company}, {_ticket.Status}.";
    }

    private async Task<string> PublishAsync()
    {
        var draft = _stored!.Draft!;
        _published = [.. _stored.Frames.Where(f => !f.ExcludedByUser && !f.SensitiveContext).Select(f => f.Id)];
        var answer = await Pipe.RequestAsync<SessionPublished>(id => new PublishSessionCommand
        {
            RequestId = id,
            SessionId = _session!,
            TicketId = Ticket,
            TicketCompany = _ticket!.Company,
            NoteType = "internal",
            Minutes = (int)Math.Max(1, draft.SuggestedTimeMinutes),
            Billable = false,
            Destinations = ["ticket_note"],
            Note = draft,
            FrameIds = _published,
        }).ConfigureAwait(false);
        var failed = answer.Results.Where(r => !r.Ok).ToList();
        return answer.Results.Count > 0 && failed.Count == 0
            ? $"{string.Join(", ", answer.Results.Select(r => $"{r.Destination} {r.Id}"))}, with {_published.Count} frame(s)."
            : throw new StepFailed($"Publish answered: {string.Join("; ", answer.Results.Select(r => $"{r.Destination} ok={r.Ok} {r.Kind} {r.Error}"))}");
    }

    private async Task<string> LandedAsync()
    {
        // Windows checks the development certificate, as the service's own client does: no callback here.
        using var http = new HttpClient { BaseAddress = backend, Timeout = TimeSpan.FromSeconds(20) };
        var shown = await http.GetFromJsonAsync<JsonElement>(new Uri($"/dev/demo-psa/tickets/{Ticket}", UriKind.Relative), Json).ConfigureAwait(false);
        var provider = shown.GetProperty("provider").GetString();
        var notes = shown.GetProperty("notes").EnumerateArray()
            .Where(n => n.GetProperty("body").GetString() is { } body && body.Contains(ScriptedLabel, StringComparison.Ordinal))
            .ToList();
        if (notes.Count == 0)
        {
            throw new StepFailed($"Ticket #{Ticket} on {provider} has {shown.GetProperty("notes").GetArrayLength()} note(s), none of them the scripted one.");
        }

        var note = notes[^1];
        var text = note.GetProperty("body").GetString()!;
        if (Leaks(text) is { } leak)
        {
            throw new StepFailed($"The note the PSA received carries the test secret ({leak}).");
        }

        if (!text.Contains("[REDACTED]", StringComparison.Ordinal) || !text.Contains("checklist", StringComparison.OrdinalIgnoreCase))
        {
            throw new StepFailed("The note the PSA received does not carry the masked window text the device sent.");
        }

        var screenshots = note.GetProperty("screenshots").GetInt32();
        return screenshots == _published.Count
            ? $"{provider} has the note on #{Ticket}: {text.Length} characters, the pass line masked, {screenshots} screenshot(s) counted (images are not kept, INV-7)."
            : throw new StepFailed($"The PSA counted {screenshots} screenshot(s) for the {_published.Count} the publish sent.");
    }

    private async Task<string> AuditAsync()
    {
        var exported = await Pipe.RequestAsync<AuditExported>(id => new ExportAuditCommand { RequestId = id }).ConfigureAwait(false);
        var copy = Path.Combine(Path.GetTempPath(), $"screentail-e2e-audit-{Guid.NewGuid():N}.json");
        try
        {
            // The UI's own way of collecting an export: copy it out, then the service's copy is removed.
            if (await AuditExporter.CollectAsync(exported.Path, copy, AuditExporter.DefaultDirectory).ConfigureAwait(false) is { } refused)
            {
                throw new StepFailed($"The export could not be collected: {refused}");
            }

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(copy).ConfigureAwait(false));
            var rows = document.RootElement.GetProperty("entries").EnumerateArray()
                .Where(r => r.GetProperty("session_id").ValueKind == JsonValueKind.String && r.GetProperty("session_id").GetString() == _session)
                .Select(r => (Type: r.GetProperty("type").GetString()!, Detail: r.GetProperty("detail").ValueKind == JsonValueKind.String ? r.GetProperty("detail").GetString() : null))
                .ToList();
            var byType = rows.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var missing = new List<string>();
            if (!byType.ContainsKey("frame_captured"))
            {
                missing.Add("frame_captured");
            }

            // The detail is the mask kind's enum name ("Password"), not the schema's wire name ("password").
            if (!rows.Any(r => r.Type == "frame_redacted" && string.Equals(r.Detail, "password", StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add("frame_redacted (password)");
            }

            if (rows.Count(r => r.Type == "bundle_sent") < 2)
            {
                missing.Add("bundle_sent for the draft and the publish");
            }

            var summary = string.Join(", ", byType.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}"));
            if (!exported.Intact)
            {
                throw new StepFailed($"The audit chain is not intact: {exported.Note}");
            }

            return missing.Count == 0
                ? $"{rows.Count} row(s) for the session: {summary}. Chain intact over {exported.Rows} row(s)."
                : throw new StepFailed($"Missing: {string.Join(", ", missing)}. Rows for the session: {summary}.");
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>Puts the technician's capture settings back as they were, whatever happened above.</summary>
    /// <summary>
    /// Puts the technician's capture settings back as they were. Once only, whichever comes first: the end
    /// of the run, or Ctrl+C (Program's CancelKeyPress handler), so an interrupted run does not leave All
    /// windows on (INV-5). The runner restores the settings file as well, in case neither got the chance.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_original is null || _pipe is null || !_pipe.IsConnected || Interlocked.Exchange(ref _restored, 1) == 1)
        {
            return;
        }

        _ = await steps.RunAsync("restore", "Settings > Capture to be put back as it was before the run", async () =>
        {
            var saved = await Pipe.SendAsync(id => new SetCaptureSettingsCommand { RequestId = id, Settings = _original }).ConfigureAwait(false);
            return saved.Ok
                ? $"All windows is {(_original.CaptureAllWindows ? "on" : "off")} again."
                : throw new StepFailed($"The restore was refused: {saved.Error}");
        }).ConfigureAwait(false);
    }

    /// <summary>The test window back in front, if anything took it. Cheap when it is already there.</summary>
    private void Front()
    {
        if (_window is not null)
        {
            _ = Native.TakeForeground(_window.Handle, TimeSpan.FromSeconds(3));
        }
    }

    private async Task<CaptureStateSnapshot?> WaitForStateAsync(Func<CaptureStateSnapshot, bool> wanted, TimeSpan within)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < within)
        {
            if (Pipe.State is { } state && wanted(state))
            {
                return state;
            }

            if (!Pipe.IsConnected)
            {
                throw new StepFailed("The pipe dropped: the capture service went away.");
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Whether a text carries the secret: whole, or any run of five or more of its letters, so an OCR that
    /// misread one digit still counts. Says which part, by length only.
    /// </summary>
    private string? Leaks(string text)
    {
        if (text.Contains(secret, StringComparison.OrdinalIgnoreCase))
        {
            return "the whole secret";
        }

        var runs = new List<string>();
        var run = new StringBuilder();
        foreach (var c in secret + " ")
        {
            if (char.IsLetter(c))
            {
                _ = run.Append(c);
                continue;
            }

            if (run.Length >= 5)
            {
                runs.Add(run.ToString());
            }

            _ = run.Clear();
        }

        return runs.FirstOrDefault(r => text.Contains(r, StringComparison.OrdinalIgnoreCase)) is { } part
            ? string.Create(CultureInfo.InvariantCulture, $"a {part.Length}-letter part of it")
            : null;
    }

    private static string Describe(Dictionary<MaskKind, int> kinds) =>
        kinds.Count == 0 ? "none" : string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"));
}
