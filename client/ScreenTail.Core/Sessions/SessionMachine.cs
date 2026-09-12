using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Sessions;

public sealed class SessionMachineOptions
{
    /// <summary>How long finalize waits for the redaction worker before purging stragglers (ST-020: 20 s).</summary>
    public TimeSpan RedactionGrace { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan RedactionPoll { get; init; } = TimeSpan.FromMilliseconds(250);
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

    public Task<bool> ResumeAsync(CancellationToken ct = default) =>
        TransitionIfAsync(s => s is SessionState.Paused, SessionState.Recording, CaptureStateReason.User, ct);

    /// <summary>Automatic pause: password field, excluded app, elevated window, sensitive context (INV-6).</summary>
    public Task<bool> SuppressAsync(CaptureStateReason reason, CancellationToken ct = default) =>
        TransitionIfAsync(s => s is SessionState.Recording, SessionState.Suppressed, reason, ct);

    /// <summary>The suppressing condition went away; capture resumes on its own.</summary>
    public Task<bool> UnsuppressAsync(CancellationToken ct = default) =>
        TransitionIfAsync(s => s is SessionState.Suppressed, SessionState.Recording, CaptureStateReason.User, ct);

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
        return true;
    }

    public async Task<bool> TryAppendTranscriptAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        var session = _session;
        if (State != SessionState.Recording || session is null)
        {
            return false;
        }

        await _store.AppendTranscriptAsync(session.Id, segment, ct).ConfigureAwait(false);
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
