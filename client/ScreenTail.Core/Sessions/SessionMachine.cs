using ScreenTail.Core.Audit;
using ScreenTail.Core.Speech;
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

    /// <summary>
    /// Where a captured frame waits to be read (ADR-0006).
    ///
    /// It used to wait in the store with <c>redaction_pending = 1</c>, which was a window nothing needed
    /// to be open: recovery purges such a frame rather than resuming it, so the store was buying a
    /// buffer rather than durability. This is that buffer, in memory, and the bytes never reach disk
    /// unredacted.
    /// </summary>
    public Capture.PendingFrames Pending { get; init; } = new();

    /// <summary>
    /// What narration each session is still owed (ST-027): segments heard while it was recording and still
    /// with the model. Finalize waits on it within the same grace as the frames, and discarding or
    /// finalizing a session forgets what is left of it.
    /// </summary>
    public NarrationBacklog Narration { get; init; } = new();
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
    /// to let go of its own hold without letting go of anybody else's. Only ever changed inside
    /// <see cref="_transitions"/>, and under <see cref="_stateLock"/> as well so that <see cref="IsHeld"/>
    /// can read it from a guard's loop. Emptied when a session ends: a hold left behind would suppress
    /// the next session, which nobody suppressed, for ever.
    /// </summary>
    private readonly HashSet<CaptureStateReason> _holds = [];
    private readonly Lock _stateLock = new();
    private SessionState _state = SessionState.Idle;
    private ActiveSession? _session;
    private string? _draftFailureReason;

    /// <summary>The ready draft's own title, for the notification (Spec §6). Null in every other state.</summary>
    private string? _draftTitle;
    private int _pendingRedactions;
    private int _draftsReady;
    private long _listenerFailures;

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

    /// <summary>
    /// How many times a <see cref="StateChanged"/> listener threw. Counts only, never the message: an
    /// exception can quote what it was handling (INV-10).
    /// </summary>
    public long ListenerFailures => Interlocked.Read(ref _listenerFailures);

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
    /// Whether this reason is holding the running session down.
    ///
    /// A guard asks this rather than remembering. Its memory was wrong in both directions: a pause
    /// made it forget a hold the session still kept, so Resume went back to a suppression nothing would
    /// lift, and a restart inside a hold's window made it remember one the new session never had
    /// (2026-10-12 review, T-41 and T-50). A pause keeps its holds; a session ending drops them.
    /// </summary>
    public bool IsHeld(CaptureStateReason reason)
    {
        lock (_stateLock)
        {
            return _holds.Contains(reason);
        }
    }

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
                    DraftTitle = _state == SessionState.DraftReady ? _draftTitle : null,
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

    /// <param name="suggestedTicket">A ticket number read off the window at start (ST-077), or null. Digits, never a title.</param>
    /// <summary>
    /// Holds every new session back until the returned hold is disposed, or answers null when a session
    /// is under way (recording, paused, suppressed or finalizing) or another hold is in force (ST-113).
    ///
    /// For Windows Installer, which ends this process to replace it: the check that no session is
    /// recording and the promise that none will start are one step under the transition lock, so no
    /// start path (the automatic one, the hotkey, the pipe) can slip a session into the gap.
    /// </summary>
    public async Task<IDisposable?> HoldStartsAsync(CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The resting states StartAsync starts from, draft_pending (#230) among them: nothing records there.
            if (State is not (SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed or SessionState.DraftPending) || _startsHeld)
            {
                return null;
            }

            _startsHeld = true;
            return new StartHold(this);
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>A hold from <see cref="HoldStartsAsync"/> is in force, so no session starts.</summary>
    public bool StartsHeld => _startsHeld;

    // Volatile: set and cleared under the transition lock, read by the pipe's start for its message.
    private volatile bool _startsHeld;

    /// <summary>Released once, by whichever dispose comes first.</summary>
    private sealed class StartHold(SessionMachine machine) : IDisposable
    {
        private SessionMachine? _machine = machine;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _machine, null) is { } held)
            {
                held._startsHeld = false;
            }
        }
    }

    public async Task<bool> StartAsync(RemoteTool tool, bool localOnly = false, string? policyVersion = null, string? suggestedTicket = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A hold from HoldStartsAsync (ST-113: Windows Installer is replacing this process) refuses
            // every start path, because they all arrive here.
            if (_startsHeld || State is not (SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed or SessionState.DraftPending))
            {
                return false;
            }

            var session = new ActiveSession(Guid.NewGuid().ToString("D"), _time.GetTimestamp(), ToolKind(tool.Kind));
            await _store.CreateSessionAsync(new NewSession(session.Id, _time.GetUtcNow(), tool, localOnly, policyVersion, suggestedTicket), ct).ConfigureAwait(false);
            _session = session;
            ClearHolds();
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
    ///
    /// The holds are read once it is this call's turn, not when it was made. Read earlier, a guard
    /// letting go in between sent the session back to Suppressed with nothing holding it, and nothing
    /// would ever lift that (2026-10-12 review).
    /// </summary>
    public async Task<bool> ResumeAsync(CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not SessionState.Paused || _session is null)
            {
                return false;
            }

            var held = _holds.Count > 0;
            await TransitionAsync(
                held ? SessionState.Suppressed : SessionState.Recording,
                held ? _holds.First() : CaptureStateReason.User,
                ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// Automatic pause: password field, excluded app, elevated window, sensitive context (INV-6).
    ///
    /// <b>A hold, not a flag.</b> Three guards watch three different conditions and all of them suppress
    /// this one session, so the question "is anything still true?" has to be asked of a set rather than
    /// of the state name. Suppressing while already suppressed used to answer false, which left the
    /// second guard believing it held nothing (2026-09-20 review).
    ///
    /// Returns true when this reason is now held, whether or not it was this call that stopped capture.
    ///
    /// A hold exists only once capture is actually off. If the transition fails before the state moves,
    /// the hold comes out again: left in, the guard would be told it held and never try again, while the
    /// session went on recording (2026-10-12 review).
    ///
    /// <b>Paused, the hold is recorded and nothing else happens.</b> Capture is already off and stays
    /// paused, and Resume finds the hold and goes to Suppressed. Refused while paused, a condition that
    /// began or came back during a pause held nothing, so Resume went to Recording over it until the
    /// guard's next tick: up to 500 ms with no indicator on screen (2026-10-12 review, round 2).
    /// </summary>
    public async Task<bool> SuppressAsync(CaptureStateReason reason, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null || State is not (SessionState.Recording or SessionState.Suppressed or SessionState.Paused))
            {
                return false;
            }

            // Before the state moves, so the recorder never sees this session stop recording without
            // also seeing why: what was being said is dropped at a suppression, not kept as at a pause.
            // Counted while paused too, where it costs at most a sentence the pause would have kept.
            _options.Narration.Suppressed();

            bool added;
            lock (_stateLock)
            {
                added = _holds.Add(reason);
            }

            if (State is SessionState.Recording)
            {
                try
                {
                    await TransitionAsync(SessionState.Suppressed, reason, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Something after the state moved (a listener) can throw too. Capture is off then,
                    // and the hold is what will lift it, so it stays.
                    if (added && State is not SessionState.Suppressed)
                    {
                        _ = ReleaseHold(reason);
                    }

                    throw;
                }
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
    ///
    /// If the transition fails before the state moves, the hold goes back. Otherwise the session stayed
    /// Suppressed with nothing holding it, the guard saw nothing left to let go of, and nothing would
    /// ever lift it (2026-10-12 review).
    /// </summary>
    public async Task<bool> UnsuppressAsync(CaptureStateReason reason, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null || !ReleaseHold(reason) || State is not SessionState.Suppressed || _holds.Count > 0)
            {
                return false;
            }

            try
            {
                await TransitionAsync(SessionState.Recording, CaptureStateReason.User, ct).ConfigureAwait(false);
            }
            catch
            {
                if (State is SessionState.Suppressed)
                {
                    RestoreHold(reason);
                }

                throw;
            }

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

            // The holds go once the session has moved, not before. Cleared first, a Finalizing write that
            // failed left the session Suppressed with nothing holding it, and nothing to lift it.
            try
            {
                await TransitionAsync(SessionState.Finalizing, CaptureStateReason.User, ct).ConfigureAwait(false);
            }
            finally
            {
                if (State is SessionState.Finalizing)
                {
                    ClearHolds();
                }
            }

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

            // Narration first, before anything is awaited: the session stays current and recording while
            // the sources stop and the store deletes it, and nothing heard or finished in that time may
            // be kept. Nothing more is heard for it, what it is still owed is not transcribed, and a
            // segment that arrives is refused.
            _session.CloseNarration();
            _ = _options.Narration.Forget(_session.Id);

            if (State is SessionState.Recording or SessionState.Paused or SessionState.Suppressed)
            {
                await _sources.StopAsync(ct).ConfigureAwait(false);
            }

            // Whatever is still queued for it goes too. Nobody has read those frames, so nobody can say
            // what is on them, and ADR-0004 is clear about such a frame.
            _options.Pending.Forget(_session.Id);
            await _store.DeleteSessionAsync(_session.Id, ct).ConfigureAwait(false);
            _session = null;
            ClearHolds();
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
    /// The outbox has settled a queued draft for good (ST-013): the backend's note is in the store
    /// (<paramref name="ready"/>), or it never will be, for <paramref name="reason"/>.
    ///
    /// Before this the outbox wrote the note to the store and nobody told the machine, so a session whose
    /// draft went to the backend stayed on the state its queueing left behind, and "Draft ready" never
    /// came. The session the machine last ran moves on to draft_ready or draft_failed when it is still
    /// resting; a draft that lands while the next session records changes only the count, never the
    /// session under way.
    /// </summary>
    /// <returns>Whether the machine's own state moved.</returns>
    public async Task<bool> DraftSettledAsync(string sessionId, bool ready, string? reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // What the store says now, not what the outbox believes. A session discarded or purged while
            // its draft was in flight has no note to be ready with and no pending draft to fail: a failure
            // is written only over draft_pending, and a ready draft only counts if the note is there. The
            // unconditional write put draft_failed over a discarded row, and the session came back in
            // History (INV-12, review of ST-013).
            string? title = null;
            bool current;
            if (ready)
            {
                // The title for the notification comes from the note the sender stored (Spec §6).
                title = (await _store.LoadSessionAsync(sessionId, ct).ConfigureAwait(false))?.Draft?.SuggestedTitle;
                current = title is not null;
            }
            else
            {
                current = await _store.SetSessionStateIfAsync(sessionId, SessionStateNames.DraftPending, CaptureStates.DraftFailed, reason, ct).ConfigureAwait(false);
            }

            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            var resting = State is SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed or SessionState.DraftPending;
            if (current && resting && string.Equals(_session?.Id, sessionId, StringComparison.Ordinal))
            {
                SetState(ready ? SessionState.DraftReady : SessionState.DraftFailed, ready ? null : reason ?? "The session could not be drafted.", title);
                return true;
            }

            // Another session's draft, or one that is gone: the count may have moved, and the tray shows
            // it, whatever the machine is doing. The state itself is left alone.
            Announce(Snapshot);
            return false;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// What a session says, in the pill and in Review, when recovery finds it draft_pending with nothing
    /// left in the outbox to settle it.
    /// </summary>
    public const string DraftNoLongerQueuedReason = "The draft was no longer on its way to the backend when ScreenTail restarted, so it will not arrive.";

    /// <summary>
    /// The kind of error that kept recovery from adopting a queued session, by type name only (INV-10),
    /// for the host to log. Null when nothing did.
    /// </summary>
    public string? AdoptionFault { get; private set; }

    /// <summary>What a session failed by retention says, in the pill and in Review.</summary>
    public const string PurgedBeforeDraftedReason = "The session's screenshots and transcript were removed by retention before it could be drafted.";

    /// <summary>
    /// Retention removed this session's raw data (ST-013). A purge takes a queued draft's outbox work with
    /// it and the store fails the session, so when the machine is resting on it in draft_pending it moves
    /// to draft_failed with the reason; the pill would otherwise go on saying the note is being drafted.
    /// Any other session, or any other state, is left alone.
    /// </summary>
    /// <returns>Whether the machine's own state moved.</returns>
    public async Task<bool> RawDataPurgedAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != SessionState.DraftPending || !string.Equals(_session?.Id, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            SetState(SessionState.DraftFailed, PurgedBeforeDraftedReason);
            return true;
        }
        finally
        {
            _transitions.Release();
        }
    }

    /// <summary>
    /// At service start: any session a crash left recording, paused, suppressed or finalizing is marked
    /// partial and taken through finalize, so its data is either drafted or cleaned up, never stranded.
    ///
    /// With nothing to finalize, the newest session whose draft is still queued (draft_pending) becomes
    /// the current session again, and the machine rests on it in DraftPending as it did before the
    /// restart. Left idle, the tray's Discard was refused and the bundle went to the backend at the next
    /// drain: the technician had lost the way to stop it (INV-12, #230 review). Its draft settling later
    /// moves the machine through <see cref="DraftSettledAsync"/> as usual. An older queued session is not
    /// adopted: before the restart the machine rested only on the last session it ran, and the older one
    /// still drains, its settling changing the count and nothing else.
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
                // No grace. Finalizing normally waits for the redaction worker to finish what it is
                // holding, and at recovery there is no worker: CaptureHost recovers before it starts
                // one, so the backlog cannot drain and the wait ends only by expiring -- twenty seconds
                // of polling the store, per orphaned session, before the service serves anything.
                //
                // Nothing is lost by not waiting. These frames are purged either way, because an
                // unredacted frame does not survive a crash (INV-1); the wait was buying a chance for
                // work that nobody was doing (2026-09-20 review).
                await FinalizeAndDraftAsync(id, lastMs, partial: true, ct, grace: TimeSpan.Zero).ConfigureAwait(false);
            }

            if (orphans.Count == 0)
            {
                await RefreshDraftCountAsync(ct).ConfigureAwait(false);

                // Oldest first, so the last is the newest. The tool and the active time come back too, so
                // the tray reads as it did before the restart.
                var queued = await _store.ListSessionsInStatesAsync([SessionStateNames.DraftPending], ct).ConfigureAwait(false);
                if (queued.Count > 0 && await _store.GetSessionSummaryAsync(queued[^1], ct).ConfigureAwait(false) is { } adopted)
                {
                    _session = new ActiveSession(adopted.Id, _time.GetTimestamp(), adopted.RemoteTool, activeMs: adopted.DurationMs ?? 0);
                    SetState(SessionState.DraftPending, null);
                }
                else
                {
                    SetState(SessionState.Idle, null);
                }
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

        // Into memory, not onto disk (ADR-0006). A full queue means redaction is losing, and the frame
        // is refused rather than queued for ever or written somewhere it would wait unredacted: the
        // count reaches the bundle, where it makes the draft hedge instead of claiming to have seen a
        // session it only partly saw.
        //
        // Counted here, against the session, the moment it is refused. The queue keeps a tally of its
        // own, but that is one number for the life of the service and nothing read it per session, so
        // a refused frame reached neither the row the bundle hedges on nor the audit log, and
        // diagnostics said 0 (2026-10-12 review, S-1). It is the same loss as a straggler purged at
        // finalize, nobody having read the frame (ADR-0004), and it is recorded the same way; the
        // store writes the audit row with the count.
        //
        // Not on the caller's token. The refusal has already happened, and the capture loop's token is
        // the one Stop cancels: a refusal that lands as Stop begins would throw before the count was
        // written and the draft would not hedge for a frame it never saw.
        //
        // A refused frame gets this purged row and no FrameCaptured row, because ST-045 counts frames
        // that were accepted. So in an audit export "purged unredacted" includes frames refused before
        // they were staged, and can be larger than "captured" for a session that lost frames this way.
        if (!_options.Pending.TryEnqueue(session.Id, frame))
        {
            await _store.RecordPurgedFramesAsync(session.Id, 1, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        // ST-045: every frame that is accepted passes through here, so this is the one place that can
        // promise the count is complete. What triggered it is a label, not content (INV-10).
        if (_store is IAuditLog audit)
        {
            await audit.RecordAsync(
                AuditTypes.FrameCaptured, session.Id, 1, AuditDetail.Of(frame.Trigger), ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The session narration is being heard for right now, and how far into it: null unless recording
    /// (INV-6), and null from the moment a discard begins. Asked by the recorder of every chunk of audio,
    /// so it is one lock and no allocation.
    ///
    /// Read under the same lock as the state, which is what makes the pair agree: a session is only ever
    /// named here while it is the one recording, never the one before or the one after.
    /// </summary>
    public NarrationSession? Hearing
    {
        get
        {
            lock (_stateLock)
            {
                return _state == SessionState.Recording && _session is { NarrationClosed: false } session
                    ? new NarrationSession(session.Id, session.BaseMs + (long)_time.GetElapsedTime(session.StartedAt).TotalMilliseconds)
                    : null;
            }
        }
    }

    /// <summary>What narration each session is still owed. The recorder fills it; finalize waits on it.</summary>
    public NarrationBacklog Narration => _options.Narration;

    /// <summary>
    /// Writes a segment that was heard in <paramref name="heardIn"/>, if that session is still taking narration.
    ///
    /// Refused when any other session is current, or none is. Accepted while the session that heard it is
    /// recording, paused, suppressed or finalizing: the recorder only cuts a segment from audio heard while
    /// recording, so the words belong to the session even when the model finishes them after a pause or a
    /// Stop, exactly as a frame taken before a pause is still redacted and written during it. Once
    /// finalize has stopped waiting for narration, nothing more is written behind the draft's back.
    /// </summary>
    public Task<bool> TryAppendTranscriptAsync(string heardIn, TranscriptSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(heardIn);
        var session = _session;
        return session is null
            || session.NarrationClosed
            || !string.Equals(session.Id, heardIn, StringComparison.Ordinal)
            || State is not (SessionState.Recording or SessionState.Paused or SessionState.Suppressed or SessionState.Finalizing)
            ? Task.FromResult(false)
            : AppendTranscriptAsync(session, segment, ct);
    }

    /// <summary>
    /// Writes a segment into the session recording right now. Refused unless one is (INV-6).
    ///
    /// For tests only. It looks the session up when the segment arrives, which is how a segment came to
    /// be stored in the session after the one it was heard in (2026-10-12 review): narration goes through
    /// the stamped overload above.
    /// </summary>
    internal Task<bool> TryAppendTranscriptAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        var session = _session;
        return State != SessionState.Recording || session is null
            ? Task.FromResult(false)
            : AppendTranscriptAsync(session, segment, ct);
    }

    private async Task<bool> AppendTranscriptAsync(ActiveSession session, TranscriptSegment segment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);

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

    private bool ReleaseHold(CaptureStateReason reason)
    {
        lock (_stateLock)
        {
            return _holds.Remove(reason);
        }
    }

    /// <summary>Puts a hold back after a release whose transition did not happen.</summary>
    private void RestoreHold(CaptureStateReason reason)
    {
        lock (_stateLock)
        {
            _ = _holds.Add(reason);
        }
    }

    private void ClearHolds()
    {
        lock (_stateLock)
        {
            _holds.Clear();
        }
    }

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

    /// <summary>
    /// Persists the transition (session row + timeline event), then updates the in-memory state and announces it.
    ///
    /// Nothing in memory moves until the transition is saved. The active-time accounting and the
    /// suppressed interval used to move first, so a state write that failed and was tried again counted
    /// active time twice, or recorded the interval as ended at the failed attempt and never recorded its
    /// real end (2026-10-12 review, round 2).
    /// </summary>
    private async Task TransitionAsync(SessionState next, CaptureStateReason reason, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("No active session.");
        var from = State;

        await _store.SetSessionStateAsync(session.Id, next.ToWire(), reason == CaptureStateReason.User ? null : ReasonName(reason), ct).ConfigureAwait(false);
        if (SchemaState(next) is { } schemaState)
        {
            await _store.AppendEventAsync(
                session.Id,
                new CaptureStateEvent { TsMs = NowMs, State = schemaState, Reason = reason == CaptureStateReason.User ? null : reason },
                ct).ConfigureAwait(false);
        }

        // ST-045: suppressed intervals, recorded where every transition already passes. The two guards
        // that cause them (ST-040's password field, ST-041's login heuristic) do not write this themselves
        // — an interval is a pair of transitions, and only the machine sees both ends. Doing it here also
        // catches the ends the guards never see: a session stopped or discarded while still suppressed.
        if (from is SessionState.Suppressed && next is not SessionState.Suppressed && _suppressedSince is { } since)
        {
            var held = (long)(_time.GetUtcNow() - since.At).TotalMilliseconds;
            if (_store is IAuditLog leaving)
            {
                await leaving.RecordAsync(
                    AuditTypes.CaptureSuppressed, session.Id, held, AuditDetail.Of(since.Reason), ct).ConfigureAwait(false);
            }

            _suppressedSince = null;
        }
        else if (next is SessionState.Suppressed && from is not SessionState.Suppressed)
        {
            _suppressedSince = (reason, _time.GetUtcNow());
        }

        session.Account(_time, from, next);
        SetState(next, null);
    }

    /// <param name="grace">
    /// How long to let the redaction worker finish what it is holding. Defaults to the configured grace;
    /// recovery passes zero, because there is no worker running then.
    /// </param>
    private async Task FinalizeAndDraftAsync(
        string sessionId,
        long durationMs,
        bool partial,
        CancellationToken ct,
        TimeSpan? grace = null)
    {
        var waitFor = grace ?? _options.RedactionGrace;
        var deadline = _time.GetTimestamp();

        // The queue, not the store: a frame that has not been redacted has not been written, so the
        // store cannot say whether one is still owed (ADR-0006). Both states count -- waiting to be
        // read, and read but not yet written.
        //
        // Narration too, within the same grace. The model takes seconds a segment, so the last things
        // said before Stop are usually still being transcribed, and the note used to be drafted without
        // them: the end of a session, which is usually its outcome (2026-10-12 review).
        while (waitFor > TimeSpan.Zero
               && (_options.Pending.DepthFor(sessionId) > 0 || _options.Narration.DepthFor(sessionId) > 0)
               && _time.GetElapsedTime(deadline) < waitFor)
        {
            await Task.Delay(_options.RedactionPoll, _time, ct).ConfigureAwait(false);
        }

        // From here the note is drawn up, so nothing more is written into the transcript, and what the
        // model has not reached is not transcribed at all. A chunk heard just before Stop but queued only
        // after the wait looked is not counted once the session is forgotten: that sentence is lost, and
        // the model is not given it.
        if (_session is { } closing && string.Equals(closing.Id, sessionId, StringComparison.Ordinal))
        {
            closing.CloseNarration();
        }

        _ = _options.Narration.Forget(sessionId);

        // Whatever is still queued when the grace runs out. The frames were captured and never read, so
        // they cannot be stored (ADR-0004) -- and there is no row to delete, so what is left to do is
        // count the loss where the bundle will find it (ADR-0006).
        var stragglers = _options.Pending.Forget(sessionId);
        await _store.RecordPurgedFramesAsync(sessionId, stragglers, ct).ConfigureAwait(false);

        // Still called: a store written by an older build may hold pending rows, and recovery is the
        // path that meets them.
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
            SetState(SessionState.DraftReady, null, outcome.Draft.SuggestedTitle);
        }
        else if (outcome.Queued)
        {
            // On its way to the backend, which is not a failure (ST-013). Nothing is being captured; the
            // machine rests in draft_pending, naming the session, until the outbox settles it
            // (DraftSettledAsync). The store is marked only if the row is still finalizing: the outbox
            // drains on its own timer, and a draft the sender has already stored stands.
            _ = await _store.SetSessionStateIfAsync(sessionId, CaptureStates.Finalizing, SessionStateNames.DraftPending, outcome.Reason, ct).ConfigureAwait(false);
            await RefreshDraftCountAsync(ct).ConfigureAwait(false);
            SetState(SessionState.DraftPending, null);
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

    /// <param name="draftTitle">The note's suggested title, for draft_ready's notification (Spec §6). Kept only in that state.</param>
    private void SetState(SessionState next, string? draftFailureReason, string? draftTitle = null)
    {
        CaptureStateSnapshot snapshot;
        lock (_stateLock)
        {
            _state = next;
            _draftFailureReason = draftFailureReason;
            _draftTitle = next == SessionState.DraftReady ? draftTitle : null;
            snapshot = Snapshot;
        }

        Announce(snapshot);
    }

    /// <summary>
    /// Tells every listener, each on its own.
    ///
    /// The guards learn that a session started from this alone now that they do not tick at idle
    /// (ST-031). A multicast invoke stops at the first listener that throws, so one faulty listener
    /// added earlier would leave every later one asleep for the whole session, and a guard asleep is a
    /// guard failing open (INV-6). A listener that throws is counted and the rest are still told; the
    /// transition, already persisted, stands.
    /// </summary>
    private void Announce(CaptureStateSnapshot snapshot)
    {
        if (StateChanged is not { } listeners)
        {
            return;
        }

        foreach (var listener in listeners.GetInvocationList().Cast<Action<CaptureStateSnapshot>>())
        {
            try
            {
                listener(snapshot);
            }
#pragma warning disable CA1031 // The point: no listener's fault may keep the next one from hearing.
            catch (Exception)
#pragma warning restore CA1031
            {
                _ = Interlocked.Increment(ref _listenerFailures);
            }
        }
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
    /// <param name="activeMs">Active time already counted: the stored duration of a session adopted at recovery, 0 otherwise.</param>
    private sealed class ActiveSession(string id, long startedAt, string? toolKind, long baseMs = 0, long activeMs = 0)
    {
        private long _activeMs = activeMs;
        private long _activeSince = startedAt;

        public string Id { get; } = id;

        public long StartedAt { get; } = startedAt;

        public string? ToolKind { get; } = toolKind;

        /// <summary>Timestamp the session's clock starts from: 0 for a new session, the last known ts_ms for a recovered one.</summary>
        public long BaseMs { get; } = baseMs;

        public bool Partial { get; init; }

        private volatile bool _narrationClosed;

        /// <summary>Set once finalize stops waiting for narration. Read by the transcript door on the recorder's thread.</summary>
        public bool NarrationClosed => _narrationClosed;

        public void CloseNarration() => _narrationClosed = true;

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
