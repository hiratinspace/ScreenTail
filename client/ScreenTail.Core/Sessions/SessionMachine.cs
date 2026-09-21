using ScreenTail.Core.Audit;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Sessions;

public sealed class SessionMachineOptions
{
    /// <summary>How long finalize waits for the redaction worker before purging stragglers (ST-020: 20 s).</summary>
    public TimeSpan RedactionGrace { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan RedactionPoll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// What scrubs speech before it is stored. There is always one: the default is the built-in patterns,
    /// and the host replaces it with the engine carrying the tenant's own.
    ///
    /// Not nullable, deliberately. The engine had a method for exactly this from the start, tested and
    /// called by nothing, because the thing that needed it could be built without it. A scrubber that
    /// has to be remembered is one that gets forgotten.
    /// </summary>
    public Privacy.RedactionEngine Scrubber { get; init; } = new();
}

/// <summary>
/// The session state machine (ST-020): idle → recording[/suppressed] → paused → finalizing → draft_ready |
/// draft_failed. Every transition is persisted and announced through <see cref="StateChanged"/>. Capture
/// sources write through the gated <c>Try*</c> methods, which accept content only while recording (INV-6).
/// Transitions are serialized; the gated writes are cheap checks against the current state.
/// </summary>
public sealed class SessionMachine : IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly ICaptureSources _sources;
    private readonly IDrafter _drafter;
    private readonly TimeProvider _time;
    private readonly SessionMachineOptions _options;
    private readonly SemaphoreSlim _transitions = new(1, 1);

    /// <summary>
    /// Which guards are currently holding capture down, by their reason (INV-6).
    ///
    /// A set rather than a flag because three guards share one suppressed state and each has to be able
    /// to let go of its own hold without letting go of anybody else's. Only ever touched inside
    /// <see cref="_transitions"/>, so it needs no lock of its own. Emptied when a session ends: a hold
    /// left behind would suppress the next session, which nobody suppressed, for ever.
    /// </summary>
    private readonly HashSet<CaptureStateReason> _holds = [];
    private readonly Lock _stateLock = new();
    private SessionState _state = SessionState.Idle;
    private ActiveSession? _session;
    private string? _draftFailureReason;
    private int _pendingRedactions;
    private int _draftsReady;

    public SessionMachine(
        ISessionStore store,
        ICaptureSources sources,
        IDrafter drafter,
        TimeProvider? time = null,
        SessionMachineOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _drafter = drafter ?? throw new ArgumentNullException(nameof(drafter));
        _time = time ?? TimeProvider.System;
        _options = options ?? new SessionMachineOptions();
    }

    /// <summary>Raised after a transition has been persisted. Handlers must not block.</summary>
    public event Action<CaptureStateSnapshot>? StateChanged;

    public SessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? SessionId => _session?.Id;

    /// <summary>
    /// The timestamp the running session started from, for turning a hook's monotonic timestamp into
    /// session time. Zero when nothing is running.
    /// </summary>
    public long SessionStartedAt => _session?.StartedAt ?? 0;

    /// <summary>Why capture is suppressed and since when, so the interval can be recorded when it ends.</summary>
    private (CaptureStateReason Reason, DateTimeOffset At)? _suppressedSince;

    /// <summary>Monotonic milliseconds since the current session started; 0 when idle.</summary>
    public long NowMs => _session is { } s ? s.BaseMs + (long)_time.GetElapsedTime(s.StartedAt).TotalMilliseconds : 0;

    public CaptureStateSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return new CaptureStateSnapshot
                {
                    State = _state.ToWire(),
                    SessionId = _state == SessionState.Idle ? null : _session?.Id,
                    ElapsedMs = _session is { } s && _state is not SessionState.Idle ? s.ActiveMs(_time, _state) : null,
                    RemoteTool = _state == SessionState.Idle ? null : _session?.ToolKind,
                    PendingRedactions = _pendingRedactions,
                    DraftsReady = _draftsReady,
                    DraftFailureReason = _state == SessionState.DraftFailed ? _draftFailureReason : null,
                };
            }
        }
    }

    /// <summary>The redaction worker (ST-041) reports its backlog; shown in the HUD and diagnostics.</summary>
    public void ReportPendingRedactions(int count)
    {
        lock (_stateLock)
        {
            _pendingRedactions = count;
        }
    }

    // ---- transitions -----------------------------------------------------------------------------------

    public async Task<bool> StartAsync(RemoteTool tool, bool localOnly = false, string? policyVersion = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed))
            {
                return false;
            }

            var session = new ActiveSession(Guid.NewGuid().ToString("D"), _time.GetTimestamp(), ToolKind(tool.Kind));
            await _store.CreateSessionAsync(new NewSession(session.Id, _time.GetUtcNow(), tool, localOnly, policyVersion), ct).ConfigureAwait(false);
            _session = session;
            _holds.Clear();
            await TransitionAsync(SessionState.Recording, CaptureStateReason.User, ct).ConfigureAwait(false);
            await _sources.StartAsync(this, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    public Task<bool> PauseAsync(CancellationToken ct = default) =>
        TransitionIfAsync(s => s is SessionState.Recording or SessionState.Suppressed, SessionState.Paused, CaptureStateReason.User, ct);

    /// <summary>
    /// The technician un-pauses. Back to suppressed, not to recording, if a guard is still holding.
    ///
    /// Suppressed → Paused → Recording was a way round every guard, reachable from the pipe, and the
    /// hotkey path already refused it (2026-09-20 review).
    /// </summary>
    public Task<bool> ResumeAsync(CancellationToken ct = default) =>
        TransitionIfAsync(
            s => s is SessionState.Paused,
            _holds.Count > 0 ? SessionState.Suppressed : SessionState.Recording,
            _holds.Count > 0 ? _holds.First() : CaptureStateReason.User,
            ct);

    /// <summary>
    /// Automatic pause: password field, excluded app, elevated window, sensitive context (INV-6).
    ///
    /// <b>A hold, not a flag.</b> Three guards watch three different conditions and all of them suppress
    /// this one session, so the question "is anything still true?" has to be asked of a set rather than
    /// of the state name. Suppressing while already suppressed used to answer false, which left the
    /// second guard believing it held nothing (2026-09-20 review).
    ///
    /// Returns true when this reason is now held, whether or not it was this call that stopped capture.
    /// </summary>
    public async Task<bool> SuppressAsync(CaptureStateReason reason, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null || State is not (SessionState.Recording or SessionState.Suppressed))
            {
                return false;
            }

            _ = _holds.Add(reason);
            if (State is SessionState.Recording)
            {
                await TransitionAsync(SessionState.Suppressed, reason, ct).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// This guard's condition went away. Capture resumes when the last one has.
    ///
    /// Unconditional before, which is the finding: the first guard to see its own condition end resumed
    /// capture for every other guard too, including one whose condition was still true and whose next
    /// poll was up to a second away. A login screen's window expiring while the technician was still
    /// typing in the password field is the ordinary way to reach that.
    /// </summary>
    public async Task<bool> UnsuppressAsync(CaptureStateReason reason, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null || !_holds.Remove(reason) || State is not SessionState.Suppressed || _holds.Count > 0)
            {
                return false;
            }

            await TransitionAsync(SessionState.Recording, CaptureStateReason.User, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// Stop and draft: finalizing → waits up to the grace period for pending redactions → purges stragglers
    /// → finalizes the session row → drafts → draft_ready or draft_failed.
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused or SessionState.Suppressed) || _session is null)
            {
                return false;
            }

            _holds.Clear();
            await TransitionAsync(SessionState.Finalizing, CaptureStateReason.User, ct).ConfigureAwait(false);
            await _sources.StopAsync(ct).ConfigureAwait(false);
            await FinalizeAndDraftAsync(_session.Id, _session.ActiveMs(_time, SessionState.Finalizing), _session.Partial, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>Stops capture and deletes the session's raw data now (typed confirmation happens in the UI).</summary>
    public async Task<bool> DiscardAsync(CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is SessionState.Idle || _session is null)
            {
                return false;
            }

            if (State is SessionState.Recording or SessionState.Paused or SessionState.Suppressed)
            {
                await _sources.StopAsync(ct).ConfigureAwait(false);
            }

            await _store.DeleteSessionAsync(_session.Id, ct).ConfigureAwait(false);
            _session = null;
            _holds.Clear();
            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            SetState(SessionState.Idle, null);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    public async Task<bool> MarkMomentAsync(CancellationToken ct = default)
    {
        if (State != SessionState.Recording || _session is null)
        {
            return false;
        }

        await _sources.MarkMomentAsync(ct).ConfigureAwait(false);
        return await TryRecordEventAsync(new MarkerEvent { TsMs = NowMs }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// At service start: any session a crash left recording, paused, suppressed or finalizing is marked
    /// partial and taken through finalize, so its data is either drafted or cleaned up, never stranded.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var orphans = await _store.ListSessionsInStatesAsync(SessionStateNames.Orphanable, ct).ConfigureAwait(false);
            foreach (var id in orphans)
            {
                var lastMs = await _store.GetLastTimestampAsync(id, ct).ConfigureAwait(false);
                // The recovered timeline continues from its last known moment, so the finalizing event stays in order.
                _session = new ActiveSession(id, _time.GetTimestamp(), null, lastMs) { Partial = true };
                await TransitionAsync(SessionState.Finalizing, CaptureStateReason.User, ct).ConfigureAwait(false);
                await FinalizeAndDraftAsync(id, lastMs, partial: true, ct).ConfigureAwait(false);
            }

            if (orphans.Count == 0)
            {
                await RefreshDraftCountAsync(ct).ConfigureAwait(false);
                SetState(SessionState.Idle, null);
            }

            return orphans.Count;
        }
        finally
        {
            _transitions.Release();
        }
    }

    // ---- gated writes (INV-6) ----------------------------------------------------------------------------

    public async Task<bool> TryRecordEventAsync(SessionEvent sessionEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (sessionEvent is CaptureStateEvent)
        {
            throw new ArgumentException("capture_state events are written by the machine itself.", nameof(sessionEvent));
        }

        var session = _session;
        if (State != SessionState.Recording || session is null)
        {
            return false;
        }

        await _store.AppendEventAsync(session.Id, sessionEvent, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryStageFrameAsync(StagedFrame frame, CancellationToken ct = default)
    {
        var session = _session;
        if (State != SessionState.Recording || session is null)
        {
            return false;
        }

        await _store.StageFrameAsync(session.Id, frame, ct).ConfigureAwait(false);

        // ST-045: every frame that reaches the store passes through here, so this is the one place that
        // can promise the count is complete. What triggered it is a label, not content (INV-10).
        if (_store is IAuditLog audit)
        {
            await audit.RecordAsync(
                AuditTypes.FrameCaptured, session.Id, 1, AuditDetail.Of(frame.Trigger), ct).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> TryAppendTranscriptAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        var session = _session;
        if (State != SessionState.Recording || session is null)
        {
            return false;
        }

        // Scrubbed here because this is the one door: every spoken segment that reaches the store comes
        // through this method, exactly as every frame comes through TryStageFrameAsync. Doing it in the
        // speech pipeline instead would leave the next caller free to forget.
        //
        // Until 2026-09-19 nothing scrubbed speech at all. ScrubText existed, was tested, and had no
        // caller, so a card number read back to a customer went into the store as spoken, and from there
        // into the bundle and to the summarizer — under a schema field described as "already scrubbed".
        var scrubbed = _options.Scrubber.ScrubText(segment.Text);
        if (!scrubbed.Complete)
        {
            // A detector gave up partway. Not-checked is not clean (ADR-0004 says the same of frames), and
            // a sentence missing from the note costs less than a password in it.
            return false;
        }

        await _store.AppendTranscriptAsync(
            session.Id,
            scrubbed.Changed ? segment with { Text = scrubbed.Text } : segment,
            ct).ConfigureAwait(false);

        // What kind and how many, never what was said (INV-10).
        if (scrubbed.Changed && _store is IAuditLog audit)
        {
            foreach (var (kind, count) in scrubbed.Counts)
            {
                await audit.RecordAsync(
                    AuditTypes.TranscriptRedacted, session.Id, count, AuditDetail.Of(kind), ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        _transitions.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- internals -------------------------------------------------------------------------------------

    private async Task<bool> TransitionIfAsync(Func<SessionState, bool> allowed, SessionState next, CaptureStateReason reason, CancellationToken ct)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!allowed(State) || _session is null)
            {
                return false;
            }

            await TransitionAsync(next, reason, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>Persists the transition (session row + timeline event), then updates the in-memory state and announces it.</summary>
    private async Task TransitionAsync(SessionState next, CaptureStateReason reason, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("No active session.");
        session.Account(_time, State, next);

        // ST-045: suppressed intervals, recorded where every transition already passes. The two guards
        // that cause them (ST-040's password field, ST-041's login heuristic) do not write this themselves
        // — an interval is a pair of transitions, and only the machine sees both ends. Doing it here also
        // catches the ends the guards never see: a session stopped or discarded while still suppressed.
        if (State is SessionState.Suppressed && next is not SessionState.Suppressed && _suppressedSince is { } since)
        {
            var held = (long)(_time.GetUtcNow() - since.At).TotalMilliseconds;
            if (_store is IAuditLog leaving)
            {
                await leaving.RecordAsync(
                    AuditTypes.CaptureSuppressed, session.Id, held, AuditDetail.Of(since.Reason), ct).ConfigureAwait(false);
            }

            _suppressedSince = null;
        }
        else if (next is SessionState.Suppressed && State is not SessionState.Suppressed)
        {
            _suppressedSince = (reason, _time.GetUtcNow());
        }

        await _store.SetSessionStateAsync(session.Id, next.ToWire(), reason == CaptureStateReason.User ? null : ReasonName(reason), ct).ConfigureAwait(false);
        if (SchemaState(next) is { } schemaState)
        {
            await _store.AppendEventAsync(
                session.Id,
                new CaptureStateEvent { TsMs = NowMs, State = schemaState, Reason = reason == CaptureStateReason.User ? null : reason },
                ct).ConfigureAwait(false);
        }

        SetState(next, null);
    }

    private async Task FinalizeAndDraftAsync(string sessionId, long durationMs, bool partial, CancellationToken ct)
    {
        var deadline = _time.GetTimestamp();
        while (await _store.CountPendingFramesAsync(sessionId, ct).ConfigureAwait(false) > 0
               && _time.GetElapsedTime(deadline) < _options.RedactionGrace)
        {
            await Task.Delay(_options.RedactionPoll, _time, ct).ConfigureAwait(false);
        }

        await _store.PurgePendingFramesAsync(sessionId, ct).ConfigureAwait(false);
        await _store.FinalizeSessionAsync(sessionId, new FinalizeInfo(durationMs, partial), ct).ConfigureAwait(false);

        DraftOutcome outcome;
        try
        {
            outcome = await _drafter.DraftAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = DraftOutcome.Failure($"Drafting failed: {ex.GetType().Name}.");
        }

        if (outcome.Draft is not null)
        {
            await _store.SaveDraftAsync(sessionId, outcome.Draft, ct).ConfigureAwait(false);
            await _store.SetSessionStateAsync(sessionId, CaptureStates.DraftReady, null, ct).ConfigureAwait(false);
            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            SetState(SessionState.DraftReady, null);
        }
        else
        {
            await _store.SetSessionStateAsync(sessionId, CaptureStates.DraftFailed, outcome.Reason, ct).ConfigureAwait(false);
            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            SetState(SessionState.DraftFailed, outcome.Reason);
        }
    }

    private async Task RefreshDraftCountAsync(CancellationToken ct)
    {
        var ready = await _store.ListSessionsInStatesAsync([CaptureStates.DraftReady], ct).ConfigureAwait(false);
        lock (_stateLock)
        {
            _draftsReady = ready.Count;
        }
    }

    private void SetState(SessionState next, string? draftFailureReason)
    {
        CaptureStateSnapshot snapshot;
        lock (_stateLock)
        {
            _state = next;
            _draftFailureReason = draftFailureReason;
            snapshot = Snapshot;
        }

        StateChanged?.Invoke(snapshot);
    }

    private static Shared.Schema.CaptureState? SchemaState(SessionState state) => state switch
    {
        SessionState.Recording => Shared.Schema.CaptureState.Recording,
        SessionState.Paused => Shared.Schema.CaptureState.Paused,
        SessionState.Suppressed => Shared.Schema.CaptureState.Suppressed,
        SessionState.Finalizing => Shared.Schema.CaptureState.Finalizing,
        _ => null,
    };

    private static string ReasonName(CaptureStateReason reason) =>
        System.Text.Json.JsonSerializer.Serialize(reason, SessionJson.Options).Trim('"');

    private static string ToolKind(RemoteToolKind kind) =>
        System.Text.Json.JsonSerializer.Serialize(kind, SessionJson.Options).Trim('"');

    /// <summary>Per-session bookkeeping: active time excludes paused and suppressed intervals (ST-066 uses it).</summary>
    private sealed class ActiveSession(string id, long startedAt, string? toolKind, long baseMs = 0)
    {
        private long _activeMs;
        private long _activeSince = startedAt;

        public string Id { get; } = id;

        public long StartedAt { get; } = startedAt;

        public string? ToolKind { get; } = toolKind;

        /// <summary>Timestamp the session's clock starts from: 0 for a new session, the last known ts_ms for a recovered one.</summary>
        public long BaseMs { get; } = baseMs;

        public bool Partial { get; init; }

        public long ActiveMs(TimeProvider time, SessionState current) =>
            _activeMs + (current is SessionState.Recording ? (long)time.GetElapsedTime(_activeSince).TotalMilliseconds : 0);

        public void Account(TimeProvider time, SessionState from, SessionState to)
        {
            if (from == SessionState.Recording && to != SessionState.Recording)
            {
                _activeMs += (long)time.GetElapsedTime(_activeSince).TotalMilliseconds;
            }
            else if (from != SessionState.Recording && to == SessionState.Recording)
            {
                _activeSince = time.GetTimestamp();
            }
        }
    }
}
