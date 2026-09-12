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

/// <summary>A message from the service to the UI.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloAck), "hello_ack")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(CommandResult), "result")]
[JsonDerivedType(typeof(StateChanged), "state_changed")]
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

    [JsonPropertyName("pending_redactions")]
    public int PendingRedactions { get; init; }

    [JsonPropertyName("drafts_ready")]
    public int DraftsReady { get; init; }

    public static CaptureStateSnapshot Idle { get; } = new() { State = CaptureStates.Idle };
}

/// <summary>State names on the wire (Spec §5 S1/S2; ST-020 owns the machine).</summary>
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
