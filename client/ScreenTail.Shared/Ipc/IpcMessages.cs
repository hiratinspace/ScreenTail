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
[JsonDerivedType(typeof(RequestConfirmationCommand), "request_confirmation")]
[JsonDerivedType(typeof(IndicatorShowingCommand), "indicator_showing")]
[JsonDerivedType(typeof(GetSessionCommand), "get_session")]
[JsonDerivedType(typeof(GetFrameCommand), "get_frame")]
[JsonDerivedType(typeof(SetFrameIncludedCommand), "set_frame_included")]
[JsonDerivedType(typeof(DeleteFrameCommand), "delete_frame")]
[JsonDerivedType(typeof(BlurFrameCommand), "blur_frame")]
[JsonDerivedType(typeof(SaveDraftCommand), "save_draft")]
[JsonDerivedType(typeof(GetIntegrationsCommand), "get_integrations")]
[JsonDerivedType(typeof(SearchTicketsCommand), "search_tickets")]
[JsonDerivedType(typeof(PublishSessionCommand), "publish_session")]
[JsonDerivedType(typeof(GetCompanyMappingsCommand), "get_company_mappings")]
[JsonDerivedType(typeof(MapCompanyCommand), "map_company")]
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

/// <summary>
/// Throws away the session being recorded, and everything captured in it.
///
/// <see cref="Confirmation"/> is a token the service issued a moment ago for this action, and without it
/// the service refuses. The spec has asked for a typed confirmation before an irreversible delete since
/// it was written; until 2026-09-20 the tray menu threw a session away on a single click, one item below
/// "Stop and draft" (Spec §3, 2026-09-19 review).
/// </summary>
public sealed record DiscardCommand : IpcCommand
{
    [JsonPropertyName("confirmation")]
    public string? Confirmation { get; init; }
}

/// <summary>
/// The UI saying it is showing the recording indicator, and where (INV-4).
///
/// Sent while it is true and not once at startup, because the interesting failure is a UI that stops
/// painting without closing: the socket stays open and the pill goes stale. The service expires a
/// report it has not heard repeated.
///
/// The rectangle is the pill's own, in virtual-screen coordinates. It is there so the claim is a
/// specific one a person could check against a screenshot rather than a bare assertion, and because the
/// UI has to compute it anyway to know whether the pill is on a screen at all.
/// </summary>
public sealed record IndicatorShowingCommand : IpcCommand
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("width")]
    public required double Width { get; init; }

    [JsonPropertyName("height")]
    public required double Height { get; init; }
}

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
public sealed record EraseAllLocalDataCommand : IpcCommand
{
    /// <summary>A token the service issued for this action, moments ago. Without it, nothing is deleted.</summary>
    [JsonPropertyName("confirmation")]
    public string? Confirmation { get; init; }
}

/// <summary>
/// Asks for a token to confirm something irreversible with (ST-085). Answered by a
/// <see cref="ConfirmationIssued"/>.
///
/// The second round trip is the defence. A confirmation the UI is trusted to have shown is one a process
/// that is not the UI does not have to show, and the pipe only proves the peer is the same user.
/// </summary>
public sealed record RequestConfirmationCommand : IpcCommand
{
    /// <summary><c>discard_session</c> or <c>erase_everything</c>. A token is good for one of them.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

/// <param name="Token">Carry it back on the destructive command. Good once, for that action, for 30 s.</param>
/// <param name="Phrase">
/// What the technician has to type to mean it. Spec §3: a typed confirmation, not a button — the point
/// is that agreeing takes an act rather than a reflex.
/// </param>
public sealed record ConfirmationIssued : IpcEvent
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("phrase")]
    public required string Phrase { get; init; }
}

// ---- Review over the pipe (ST-085 remainder, 2026-09-25) ----------------------------------------------
//
// The UI has no store. Until these existed the Review views were fed only by the screenshot harness, and
// the shell's Review area showed the pane's name. Everything a technician does to a session in Review is
// a question or a command here; the service, the one process that reads a frame, does the work.

/// <summary>Asks for a session to review. Answered by a <see cref="SessionLoaded"/>, or a failed result when there is no such session.</summary>
public sealed record GetSessionCommand : IpcCommand
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }
}

/// <summary>
/// A session as the store hands it out: redacted frames only, with the draft (INV-1). The same
/// <c>session.v1</c> shape the fixtures and the bundle use, so there is one serialiser for it.
/// </summary>
public sealed record SessionLoaded : IpcEvent
{
    [JsonPropertyName("session")]
    public required Schema.Session Session { get; init; }
}

/// <summary>Asks for one redacted frame's image. Answered by a <see cref="FrameLoaded"/>.</summary>
public sealed record GetFrameCommand : IpcCommand
{
    [JsonPropertyName("frame_id")]
    public required string FrameId { get; init; }
}

/// <summary>
/// One frame's bytes. Also the answer to a <see cref="BlurFrameCommand"/>, carrying the image as it now is.
///
/// One frame per message because the pipe's frame limit is 1 MiB and a redacted screenshot is a few
/// hundred kilobytes; a whole session's images in one message would be the first thing to exceed it.
/// </summary>
public sealed record FrameLoaded : IpcEvent
{
    [JsonPropertyName("frame_id")]
    public required string FrameId { get; init; }

    [JsonPropertyName("image")]
    public required byte[] Image { get; init; }
}

/// <summary>Space in the filmstrip: whether the frame goes out with the note (Spec §5 S3).</summary>
public sealed record SetFrameIncludedCommand : IpcCommand
{
    [JsonPropertyName("frame_id")]
    public required string FrameId { get; init; }

    [JsonPropertyName("included")]
    public required bool Included { get; init; }
}

/// <summary>Delete, once the five-second undo has closed. A failed result means it was already gone.</summary>
public sealed record DeleteFrameCommand : IpcCommand
{
    [JsonPropertyName("frame_id")]
    public required string FrameId { get; init; }
}

/// <summary>
/// Blur a rectangle of a frame, in the frame's own pixels. Answered by a <see cref="FrameLoaded"/> with the
/// image as it now is.
///
/// The service paints it, with the same masker redaction uses, and writes it before answering. The UI
/// used to flatten in WPF and hand the bytes back over; that re-encoded a JPEG as PNG on the UI thread
/// and made the stored name a lie (weaknesses P2-9). Destructive: the pixels underneath stop existing.
/// </summary>
public sealed record BlurFrameCommand : IpcCommand
{
    [JsonPropertyName("frame_id")]
    public required string FrameId { get; init; }

    [JsonPropertyName("x")]
    public required long X { get; init; }

    [JsonPropertyName("y")]
    public required long Y { get; init; }

    [JsonPropertyName("width")]
    public required long Width { get; init; }

    [JsonPropertyName("height")]
    public required long Height { get; init; }
}

/// <summary>The edited note, written back (ST-074's autosave). The whole note, every time: one write, one shape.</summary>
public sealed record SaveDraftCommand : IpcCommand
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("draft")]
    public required Schema.DraftNote Draft { get; init; }
}

// ---- Publishing over the pipe (ST-078, ST-093, ST-094; 2026-09-25) ------------------------------------
//
// The UI never talks to the backend and never learns which PSA the tenant runs. It asks the service,
// which holds the device token and the egress guard, and the service asks the backend. Every answer is
// the backend's answer passed through; a refusal arrives as a failed result with the backend's words.

/// <summary>What the tenant has connected. Answered by an <see cref="IntegrationsListed"/>.</summary>
public sealed record GetIntegrationsCommand : IpcCommand;

/// <param name="Secret">The last four characters behind bullets, as the backend shows them. Never more.</param>
public sealed record IntegrationInfo(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("site_url")] string SiteUrl,
    [property: JsonPropertyName("secret")] string Secret);

public sealed record IntegrationsListed : IpcEvent
{
    [JsonPropertyName("integrations")]
    public required IReadOnlyList<IntegrationInfo> Integrations { get; init; }
}

/// <summary>The ticket picker's search: three characters or a number. Answered by a <see cref="TicketsFound"/>.</summary>
public sealed record SearchTicketsCommand : IpcCommand
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }
}

public sealed record TicketRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("company")] string Company,
    [property: JsonPropertyName("status")] string? Status = null);

public sealed record TicketsFound : IpcEvent
{
    [JsonPropertyName("tickets")]
    public required IReadOnlyList<TicketRow> Tickets { get; init; }
}

/// <summary>
/// Publish (INV-3: only ever because the technician pressed it). The note as edited, which frames go,
/// and where. The service reads the frames' bytes from the store; the UI never holds them for this.
/// Answered by a <see cref="SessionPublished"/> with each destination on its own.
/// </summary>
public sealed record PublishSessionCommand : IpcCommand
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("ticket_id")]
    public required string TicketId { get; init; }

    /// <summary>The PSA's name for the ticket's company, as the search returned it, for the knowledge-base mapping (ST-097).</summary>
    [JsonPropertyName("ticket_company")]
    public string? TicketCompany { get; init; }

    /// <summary><c>internal</c> or <c>discussion</c>.</summary>
    [JsonPropertyName("note_type")]
    public required string NoteType { get; init; }

    [JsonPropertyName("minutes")]
    public required int Minutes { get; init; }

    [JsonPropertyName("billable")]
    public bool Billable { get; init; } = true;

    /// <summary><c>ticket_note</c>, <c>time_entry</c>, <c>kb_article</c>.</summary>
    [JsonPropertyName("destinations")]
    public required IReadOnlyList<string> Destinations { get; init; }

    [JsonPropertyName("note")]
    public required Schema.DraftNote Note { get; init; }

    /// <summary>The included frames, in strip order. Only redacted frames have bytes to send (INV-1).</summary>
    [JsonPropertyName("frame_ids")]
    public required IReadOnlyList<string> FrameIds { get; init; }
}

public sealed record PublishOutcomeRow(
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("link")] string? Link = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("retryable")] bool Retryable = false);

public sealed record SessionPublished : IpcEvent
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<PublishOutcomeRow> Results { get; init; }
}

// ---- The company mapping (ST-097; 2026-09-25) ---------------------------------------------------------
//
// A knowledge-base article is filed under the ticket's company in the documentation platform. When the
// backend has no mapping for the PSA's name of it, the publish answers `needs_mapping` and the pane asks
// once, at publish, which of the platform's companies it is. The answer is remembered by the backend.

/// <summary>The documentation platform's companies and what is mapped so far. Answered by a <see cref="CompanyMappingsListed"/>.</summary>
public sealed record GetCompanyMappingsCommand : IpcCommand;

public sealed record CompanyChoiceRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record CompanyMappingRow(
    [property: JsonPropertyName("psa_company")] string PsaCompany,
    [property: JsonPropertyName("doc_company_id")] string DocCompanyId,
    [property: JsonPropertyName("doc_company_name")] string DocCompanyName,
    [property: JsonPropertyName("confidence")] string Confidence);

public sealed record CompanyMappingsListed : IpcEvent
{
    [JsonPropertyName("companies")]
    public required IReadOnlyList<CompanyChoiceRow> Companies { get; init; }

    [JsonPropertyName("mappings")]
    public required IReadOnlyList<CompanyMappingRow> Mappings { get; init; }
}

/// <summary>The technician's answer to the prompt: this PSA company is that platform company. Answered by a <c>result</c>.</summary>
public sealed record MapCompanyCommand : IpcCommand
{
    [JsonPropertyName("psa_company")]
    public required string PsaCompany { get; init; }

    [JsonPropertyName("doc_company_id")]
    public required string DocCompanyId { get; init; }
}

/// <summary>A message from the service to the UI.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloAck), "hello_ack")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(CommandResult), "result")]
[JsonDerivedType(typeof(StateChanged), "state_changed")]
[JsonDerivedType(typeof(CapabilitiesReported), "capabilities")]
[JsonDerivedType(typeof(DiagnosticsReported), "diagnostics")]
[JsonDerivedType(typeof(SessionsListed), "sessions")]
[JsonDerivedType(typeof(ConfirmationIssued), "confirmation")]
[JsonDerivedType(typeof(SessionLoaded), "session")]
[JsonDerivedType(typeof(FrameLoaded), "frame")]
[JsonDerivedType(typeof(IntegrationsListed), "integrations")]
[JsonDerivedType(typeof(TicketsFound), "tickets")]
[JsonDerivedType(typeof(SessionPublished), "published")]
[JsonDerivedType(typeof(CompanyMappingsListed), "company_mappings")]
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

    /// <summary>
    /// The draft's own suggested title, for the "Draft ready" notification (Spec §6). Present only in
    /// <c>draft_ready</c>.
    ///
    /// This is model-written text about a session the technician is about to review, and it crosses the
    /// pipe to the same user's UI on the same machine. It is not a window title and never becomes one:
    /// INV-10 is about what reaches logs, metrics and the backend, and none of those see this.
    /// </summary>
    [JsonPropertyName("draft_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DraftTitle { get; init; }

    /// <summary>The company the session's ticket belongs to, so a notification names the customer.</summary>
    [JsonPropertyName("draft_company")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DraftCompany { get; init; }

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
