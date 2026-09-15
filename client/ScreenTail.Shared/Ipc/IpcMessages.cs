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

/// <summary>A message from the service to the UI.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloAck), "hello_ack")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(CommandResult), "result")]
[JsonDerivedType(typeof(StateChanged), "state_changed")]
[JsonDerivedType(typeof(CapabilitiesReported), "capabilities")]
public abstract record IpcEvent;

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
    [JsonPropertyName("request_id")]
    public required int RequestId { get; init; }

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
    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestId { get; init; }

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
