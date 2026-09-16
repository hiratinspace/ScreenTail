using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Ipc;

// The service-UI contract (docs/ipc-contract.md). Same conventions as the session schema: snake_case
// on the wire, "type" discriminators, unknown members rejected, optional values omitted.

/// <summary>A command from the UI to the service. Each one is answered by a <see cref="CommandResult"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloCommand), "hello")]
[JsonDerivedType(typeof(StartCommand), "start")]
[JsonDerivedType(typeof(PauseCommand), "pause")]
[JsonDerivedType(typeof(ResumeCommand), "resume")]
[JsonDerivedType(typeof(StopCommand), "stop")]
[JsonDerivedType(typeof(DiscardCommand), "discard")]
[JsonDerivedType(typeof(MarkMomentCommand), "mark_moment")]
[JsonDerivedType(typeof(GetStateCommand), "get_state")]
[JsonDerivedType(typeof(GetCapabilitiesCommand), "get_capabilities")]
[JsonDerivedType(typeof(GetDiagnosticsCommand), "get_diagnostics")]
[JsonDerivedType(typeof(ListSessionsCommand), "list_sessions")]
[JsonDerivedType(typeof(EraseAllLocalDataCommand), "erase_all_local_data")]
public abstract record IpcCommand
{
    [JsonPropertyName("request_id")]
    public required int RequestId { get; init; }
}

public sealed record HelloCommand : IpcCommand
{
    [JsonPropertyName("contract_version")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("client_name")]
    public required string ClientName { get; init; }

    [JsonPropertyName("client_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientVersion { get; init; }
}

public sealed record StartCommand : IpcCommand;

public sealed record PauseCommand : IpcCommand;

public sealed record ResumeCommand : IpcCommand;

public sealed record StopCommand : IpcCommand;

public sealed record DiscardCommand : IpcCommand;

public sealed record MarkMomentCommand : IpcCommand;

public sealed record GetStateCommand : IpcCommand;

/// <summary>Asks the service to re-check what Windows allows (ST-021). Answered by a <see cref="CapabilitiesReported"/>.</summary>
public sealed record GetCapabilitiesCommand : IpcCommand;

/// <summary>
/// Asks what is being captured right now (ST-071, ST-085). Answered by a <see cref="DiagnosticsReported"/>.
///
/// The panel it feeds is what a technician turns to a customer and shows, so the answer has to come from
/// the service rather than from anything the UI believes: a panel that says "local-only: yes" from a
/// literal in the UI process is worse than no panel at all (weaknesses P1-2).
/// </summary>
public sealed record GetDiagnosticsCommand : IpcCommand;

/// <summary>
/// Asks for the session list behind the History screen (ST-079, ST-085). Answered by a
/// <see cref="SessionsListed"/>.
///
/// The UI does not open the store. The service owns it, and it is the only process that reads a frame,
/// so INV-1's read-path filtering has one owner rather than one per window.
/// </summary>
public sealed record ListSessionsCommand : IpcCommand
{
    /// <summary>How many rows at most. The screen pages; the pipe does not stream.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 200;
}

/// <summary>
/// Deletes every captured session and the tokens with them (INV-12).
///
/// The path existed in <c>LocalDataEraser</c> and was unreachable: nothing in the UI and no command on
/// the pipe called it, so "delete everything" was a promise the product could not keep. ST-081 builds the
/// Settings screen that asks for confirmation; this is the command behind the button.
/// </summary>
public sealed record EraseAllLocalDataCommand : IpcCommand;

/// <summary>A message from the service to the UI.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloAck), "hello_ack")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(CommandResult), "result")]
[JsonDerivedType(typeof(StateChanged), "state_changed")]
[JsonDerivedType(typeof(CapabilitiesReported), "capabilities")]
[JsonDerivedType(typeof(DiagnosticsReported), "diagnostics")]
[JsonDerivedType(typeof(SessionsListed), "sessions")]
public abstract record IpcEvent
{
    /// <summary>
    /// The command this answers, when it answers one. Null for events the service volunteers, such as
    /// <see cref="StateChanged"/>.
    ///
    /// On the base type so that one rule covers every reply: the client completes the pending request
    /// whose id matches, whatever the event's type. Without it each new request/reply pair needed its own
    /// plumbing, and <c>get_capabilities</c> — which has had a reply type since ST-021 — would have hung
    /// the first time anything asked, because the read loop only ever completed a <see cref="CommandResult"/>.
    /// </summary>
    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestId { get; init; }
}

public sealed record HelloAck : IpcEvent
{
    [JsonPropertyName("contract_version")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("service_version")]
    public required string ServiceVersion { get; init; }

    [JsonPropertyName("state")]
    public required CaptureStateSnapshot State { get; init; }
}

/// <summary>The handshake failed; the service closes the pipe after sending this.</summary>
public sealed record Rejected : IpcEvent
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record CommandResult : IpcEvent
{
    /// <summary>Always present on a result, unlike on the base type, where an unsolicited event has none.</summary>
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

public sealed record StateChanged : IpcEvent
{
    [JsonPropertyName("state")]
    public required CaptureStateSnapshot State { get; init; }
}

/// <summary>Rejection reasons on the wire. Audit rows record the reason only.</summary>
public static class RejectReasons
{
    public const string UnverifiedClient = "unverified_client";
    public const string BadToken = "bad_token";
    public const string ContractMismatch = "contract_mismatch";
    public const string Protocol = "protocol";
}

/// <summary>What every open UI shows. ST-020 owns the transitions and extends this.</summary>
public sealed record CaptureStateSnapshot
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("elapsed_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ElapsedMs { get; init; }

    [JsonPropertyName("remote_tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteTool { get; init; }

    /// <summary>
    /// Why capture is not running, when the state alone does not say (ST-072). A wire name from
    /// <c>CaptureStateReason</c> in session.v1.json — <c>password_field</c>, <c>excluded_app</c>,
    /// <c>elevated_window</c>, <c>sensitive_context</c>, <c>out_of_scope</c>, <c>user</c>.
    ///
    /// The HUD needs it because Spec §5 S2 gives each one different words, and a technician being told
    /// "paused" without being told why cannot know whether to do anything about it.
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    /// The process in front when capture is out of scope, so the HUD can say "Not capturing — Outlook".
    ///
    /// A process name, never a window title: titles are content (INV-10) and this crosses a pipe and
    /// reaches a screen a customer may be looking at. session.v1.json draws the same line in FocusEvent.
    /// </summary>
    [JsonPropertyName("scope_process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScopeProcess { get; init; }

    [JsonPropertyName("pending_redactions")]
    public int PendingRedactions { get; init; }

    [JsonPropertyName("drafts_ready")]
    public int DraftsReady { get; init; }

    /// <summary>Why the last draft failed; present only in <c>draft_failed</c>. Plain language, no content.</summary>
    [JsonPropertyName("draft_failure_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DraftFailureReason { get; init; }

    public static CaptureStateSnapshot Idle { get; } = new() { State = CaptureStates.Idle };
}

/// <summary>State names on the wire (Spec §5 S1/S2; ST-020 owns the machine).</summary>
/// <summary>What Windows is allowing right now (ST-021). Sent on request and whenever a check changes.</summary>
public sealed record CapabilitiesReported : IpcEvent
{
    [JsonPropertyName("checked_at")]
    public required DateTimeOffset CheckedAt { get; init; }

    [JsonPropertyName("can_capture")]
    public required bool CanCapture { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<CapabilityStatus> Checks { get; init; }
}

/// <param name="Capability">One of <c>desktop_session</c>, <c>microphone</c>, <c>screen_capture</c>, <c>input_hooks</c>, <c>elevated_windows</c>.</param>
/// <param name="State">One of <c>ok</c>, <c>degraded</c>, <c>blocked</c>, <c>unknown</c>.</param>
public sealed record CapabilityStatus
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>Shown as-is in the HUD and diagnostics. Never contains anything captured (INV-10).</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("fix_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixHint { get; init; }

    /// <summary>A Windows Settings deep link for the HUD's "Fix" button, e.g. <c>ms-settings:privacy-microphone</c>.</summary>
    [JsonPropertyName("fix_link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixLink { get; init; }
}

public static class CaptureStates
{
    public const string Idle = "idle";
    public const string Recording = "recording";
    public const string Paused = "paused";
    public const string Suppressed = "suppressed";
    public const string Finalizing = "finalizing";
    public const string DraftReady = "draft_ready";
    public const string DraftFailed = "draft_failed";
}

/// <summary>
/// What the "What's being captured right now?" panel shows (ST-071, ST-085).
///
/// Every field is a state, a count or a device name. Nothing here has ever been on a customer's screen:
/// the active window is named by its process, never by its title, because this text goes on a clipboard
/// and into tickets (INV-10).
/// </summary>
public sealed record DiagnosticsReported : IpcEvent
{
    /// <summary>The scope decision's own words: a process name and what is being done about it.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>The device name, or null when there is none. A device name, never audio.</summary>
    [JsonPropertyName("microphone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Microphone { get; init; }

    [JsonPropertyName("suppression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suppression { get; init; }

    [JsonPropertyName("redaction_backlog")]
    public required int RedactionBacklog { get; init; }

    /// <summary>Frames deleted because they could not be checked (INV-1).</summary>
    [JsonPropertyName("frames_dropped")]
    public required long FramesDropped { get; init; }

    /// <summary>Keyboard events dropped for being out of scope (INV-6).</summary>
    [JsonPropertyName("keystrokes_dropped")]
    public required long KeystrokesDropped { get; init; }

    /// <summary>Requests the egress guard refused (INV-8).</summary>
    [JsonPropertyName("egress_blocked")]
    public required long EgressBlocked { get; init; }

    [JsonPropertyName("local_only")]
    public required bool LocalOnly { get; init; }

    [JsonPropertyName("policy_version")]
    public required string PolicyVersion { get; init; }

    [JsonPropertyName("cpu_percent")]
    public required double CpuPercent { get; init; }

    [JsonPropertyName("working_set_bytes")]
    public required long WorkingSetBytes { get; init; }

    [JsonPropertyName("service_version")]
    public required string ServiceVersion { get; init; }
}

/// <summary>The History screen's rows (ST-079), read from the store by the service.</summary>
public sealed record SessionsListed : IpcEvent
{
    [JsonPropertyName("sessions")]
    public required IReadOnlyList<SessionRow> Sessions { get; init; }
}

/// <summary>
/// One row of the History screen (Spec §5 S4).
///
/// The status is computed by the service rather than derived here from the note, so the draft's text
/// never crosses the pipe to be displayed as a status. What the screen shows is a state, a count and a
/// process name.
/// </summary>
public sealed record SessionRow
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public required long DurationMs { get; init; }

    /// <summary>One of <c>draft</c>, <c>published</c>, <c>discarded</c>, <c>partial</c>, <c>pending</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    /// <summary>Redacted frames only. A pending frame is not one anything outside redaction may count (INV-1).</summary>
    [JsonPropertyName("frames")]
    public required int Frames { get; init; }

    /// <summary>Frames deleted because they could not be checked, so Review can show the gap rather than hide it.</summary>
    [JsonPropertyName("frames_purged")]
    public required long FramesPurged { get; init; }
}
